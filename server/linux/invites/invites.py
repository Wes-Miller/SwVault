#!/usr/bin/env python3
"""
SwVault team service: invites, school-email verification and member profiles, next to Gitea.

Runs as docker-compose.yml service "invites", published by Tailscale Funnel under
https://<server>/swvault-invites/. Python standard library only.

Joining (public)
  GET  /invite/<code>       {team, valid, role, emailDomains, subteams} for the sign-in window
  POST /verify              {code, email} -> emails a 6-digit code (when EMAIL_DOMAINS is set)
  POST /redeem              {code, username, password, fullName?, email?, emailCode?,
                             memberType: member|lead, subteam?} -> creates the Gitea account,
                            adds it to the invite's team (Designers or Viewers), saves the profile
  GET  /download[?invite=C] the team's SwVault installer zip (named with the invite code, which the
                            installer picks up so the member doesn't have to type it)

Members (HTTP Basic: Gitea user name + SwVault token or password; must be in the organization)
  GET  /people              everyone on the team with their profile (member or lead, subteam)
  PUT  /people/me           {memberType: member|lead, subteam?}

  POST /reviews/<n>/notify  emails the lead (new request) or the requester (approved / changes
                            requested, with the lead's feedback); each event is emailed once

Admins (as members, and in the organization's Owners team)
  POST   /invites           {role: designer|viewer, uses, days, note?} -> new invite
  GET    /invites           active invites
  DELETE /invites/<code>    revoke
  PUT    /installer         upload the installer zip (scripts/package.ps1 -Publish)
  PUT    /people/<login>    set someone else's profile
  POST   /test-email        {to} -> sends a test message (swvault-admin.sh test-email)
"""

import hmac
import json
import os
import re
import secrets
import shutil
import smtplib
import ssl
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from email.message import EmailMessage
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

GITEA = os.environ.get("GITEA_URL", "http://127.0.0.1:3000").rstrip("/")
ORG = os.environ["ORG"]
TEAM_NAME = os.environ.get("TEAM_NAME", ORG)
REPO = os.environ.get("REPO", "cad")
TOKEN_FILE = os.environ.get("ADMIN_TOKEN_FILE", "/run/swvault/admin-token")
STATE_DIR = os.environ.get("STATE_DIR", "/data")
INVITES_FILE = os.path.join(STATE_DIR, "invites.json")
PEOPLE_FILE = os.path.join(STATE_DIR, "people.json")
NOTIFIED_FILE = os.path.join(STATE_DIR, "review-emails.json")
INSTALLER_FILE = os.path.join(STATE_DIR, "installer", "SwVault-installer.zip")
PORT = int(os.environ.get("PORT", "3100"))
PREFIX = "/swvault-invites"

# New members must prove they own an address at one of these domains (e.g. "colorado.edu").
# Empty: no email check.
EMAIL_DOMAINS = [d.strip().lower().lstrip("@") for d in os.environ.get("EMAIL_DOMAINS", "").split(",")
                 if d.strip() and d.strip().lower() != "none"]
SMTP_HOST = os.environ.get("SMTP_HOST", "")
SMTP_PORT = int(os.environ.get("SMTP_PORT") or 587)
SMTP_USER = os.environ.get("SMTP_USER", "")
SMTP_PASSWORD = os.environ.get("SMTP_PASSWORD", "")
SMTP_FROM = os.environ.get("SMTP_FROM", "") or SMTP_USER

MAX_UPLOAD = 2 * 1024 ** 3  # installer zip
CODE_ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"  # no 0/O, 1/I
USERNAME_RE = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,38}[A-Za-z0-9])?$")
EMAIL_RE = re.compile(r"^[A-Za-z0-9._%+'-]+@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)$")
ROLES = {"designer": "Designers", "viewer": "Viewers"}
MEMBER_TYPES = ("member", "lead")

VERIFY_MINUTES = 15
VERIFY_ATTEMPTS = 5
RESEND_SECONDS = 60
SENDS_PER_HOUR = 5

_lock = threading.Lock()
_failures = {}       # "bucket:ip" -> [timestamps] of failed attempts
_verifications = {}  # email (lowercase) -> {code, invite, expires, attempts, sent: [timestamps]}
FAIL_WINDOW = 15 * 60
FAIL_LIMIT = 10


def log(message):
    print(f"{datetime.now().isoformat(timespec='seconds')} {message}", flush=True)


class ApiError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status
        self.message = message


# ------------------------------------------------------------------ state

def load(path, empty):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return empty


def save(path, data):
    os.makedirs(STATE_DIR, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    os.replace(tmp, path)


def load_invites():
    return load(INVITES_FILE, {"invites": []})


def load_people():
    return load(PEOPLE_FILE, {"people": {}})


def now():
    return datetime.now(timezone.utc)


def stamp():
    return now().isoformat(timespec="seconds")


def is_active(invite):
    return invite["usesLeft"] > 0 and datetime.fromisoformat(invite["expires"]) > now()


def normalize_code(code):
    return re.sub(r"[^A-Z0-9]", "", (code or "").upper())


def find_invite(state, code):
    wanted = normalize_code(code)
    return next((i for i in state["invites"] if normalize_code(i["code"]) == wanted), None) if wanted else None


def new_code():
    raw = "".join(secrets.choice(CODE_ALPHABET) for _ in range(12))
    return f"{raw[0:4]}-{raw[4:8]}-{raw[8:12]}"


def public_invite(invite):
    return {k: invite[k] for k in ("code", "role", "usesLeft", "uses", "expires", "note", "createdBy", "createdAt") if k in invite}


def clean_profile(body):
    member_type = (body.get("memberType") or "member").lower()
    if member_type not in MEMBER_TYPES:
        raise ApiError(HTTPStatus.BAD_REQUEST, "memberType must be member or lead.")
    subteam = re.sub(r"\s+", " ", (body.get("subteam") or "")).strip()[:60]
    if member_type == "lead" and not subteam:
        raise ApiError(HTTPStatus.BAD_REQUEST, "Say which subteam you lead (e.g. Chassis, Aero, Powertrain).")
    return member_type, subteam


def known_subteams(people):
    names = {p["subteam"] for p in people["people"].values() if p.get("subteam")}
    return sorted(names, key=str.lower)


def save_profile(login, member_type, subteam):
    with _lock:
        people = load_people()
        people["people"][login.lower()] = {"login": login, "memberType": member_type, "subteam": subteam, "updated": stamp()}
        save(PEOPLE_FILE, people)


# ------------------------------------------------------------------ throttling

# Separate buckets per client address (redeem guesses, email sends, sign-ins), so someone guessing
# invite codes can't lock the admin out. None = local request (admin script), never throttled.

def throttled(key):
    if key is None:
        return False
    with _lock:
        recent = [t for t in _failures.get(key, []) if t > time.time() - FAIL_WINDOW]
        _failures[key] = recent
        return len(recent) >= FAIL_LIMIT


def record_failure(key):
    if key is None:
        return
    with _lock:
        _failures.setdefault(key, []).append(time.time())


def check_throttle(key, message="Too many failed attempts. Try again in 15 minutes."):
    if throttled(key):
        raise ApiError(HTTPStatus.TOO_MANY_REQUESTS, message)


# ------------------------------------------------------------------ email

def email_domain_allowed(email):
    match = EMAIL_RE.match(email or "")
    if not match:
        return False
    domain = match.group(1).lower()
    return any(domain == d or domain.endswith("." + d) for d in EMAIL_DOMAINS)


def domains_text():
    return " or ".join("@" + d for d in EMAIL_DOMAINS)


def send_email(to, subject, text):
    if not SMTP_HOST:
        raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "This server can't send email yet. Ask your admin to set up email (run setup.sh again).")
    message = EmailMessage()
    message["From"] = f"SwVault {TEAM_NAME} <{SMTP_FROM}>"
    message["To"] = to
    message["Subject"] = subject
    message.set_content(text)
    context = ssl.create_default_context()
    try:
        if SMTP_PORT == 465:
            server = smtplib.SMTP_SSL(SMTP_HOST, SMTP_PORT, timeout=30, context=context)
        else:
            server = smtplib.SMTP(SMTP_HOST, SMTP_PORT, timeout=30)
            server.ehlo()
            if server.has_extn("starttls"):
                server.starttls(context=context)
                server.ehlo()
        with server:
            if SMTP_USER:
                server.login(SMTP_USER, SMTP_PASSWORD)
            server.send_message(message)
    except (smtplib.SMTPException, OSError) as e:
        log(f"sending email to {to} failed: {e!r}")
        raise ApiError(HTTPStatus.BAD_GATEWAY, "Couldn't send the email right now. Try again in a few minutes, or ask your admin.")


def start_verification(handler, body):
    key = handler.throttle_key("email")
    check_throttle(key)
    email = (body.get("email") or "").strip()
    if not EMAIL_DOMAINS:
        return HTTPStatus.OK, {"required": False}
    with _lock:
        invite = find_invite(load_invites(), body.get("code"))
    if not invite or not is_active(invite):
        record_failure(key)
        raise ApiError(HTTPStatus.NOT_FOUND, "That invite code isn't valid (it may have expired or been used up). Ask your admin for a new one.")
    if not email_domain_allowed(email):
        raise ApiError(HTTPStatus.BAD_REQUEST, f"Use your {domains_text()} email address.")
    if email_has_account(email):
        raise ApiError(HTTPStatus.CONFLICT, f"There's already an account for {email}. Choose \"I have an account\" and sign in, or ask your admin to reset your password.")

    address = email.lower()
    with _lock:
        pending = _verifications.get(address, {})
        sent = [t for t in pending.get("sent", []) if t > time.time() - 3600]
        if sent and sent[-1] > time.time() - RESEND_SECONDS:
            raise ApiError(HTTPStatus.TOO_MANY_REQUESTS, "We just sent a code. Check your inbox (and junk folder), or wait a minute to send another.")
        if len(sent) >= SENDS_PER_HOUR:
            raise ApiError(HTTPStatus.TOO_MANY_REQUESTS, "Too many codes sent to this address. Try again in an hour.")
        code = f"{secrets.randbelow(1_000_000):06d}"
        _verifications[address] = {
            "code": code, "invite": normalize_code(invite["code"]), "attempts": 0,
            "expires": time.time() + VERIFY_MINUTES * 60, "sent": sent + [time.time()],
        }
    send_email(email, f"Your {TEAM_NAME} SwVault code: {code}",
               f"Your code to join the {TEAM_NAME} vault is:\n\n    {code}\n\n"
               f"Type it into the SwVault window. It works for {VERIFY_MINUTES} minutes.\n\n"
               "If you didn't ask for this, you can ignore this email.\n")
    log(f"verification code sent to {address} for invite {invite['code']}")
    return HTTPStatus.OK, {"required": True, "sent": True, "minutes": VERIFY_MINUTES}


def check_verification(key, email, email_code, invite):
    """Call with _lock held. The caller removes the pending code once the account exists, so a
    taken user name doesn't cost the member their email code."""
    address = (email or "").strip().lower()
    pending = _verifications.get(address)
    if not pending or pending["expires"] < time.time() or pending["invite"] != normalize_code(invite["code"]):
        raise ApiError(HTTPStatus.BAD_REQUEST, "Get a code sent to your email first (or ask for a new one; codes last 15 minutes).")
    pending["attempts"] += 1
    if not hmac.compare_digest(pending["code"], re.sub(r"\D", "", email_code or "")):
        if key is not None:
            _failures.setdefault(key, []).append(time.time())
        if pending["attempts"] >= VERIFY_ATTEMPTS:
            del _verifications[address]
            raise ApiError(HTTPStatus.BAD_REQUEST, "Too many wrong codes. Ask for a new one.")
        raise ApiError(HTTPStatus.BAD_REQUEST, "That email code isn't right. Check the latest email we sent.")


def email_has_account(email):
    status, found = gitea("GET", "admin/emails/search?limit=10&q=" + urllib.request.quote(email))
    return status == 200 and any((e.get("email") or "").lower() == email.lower() for e in found or [])


# ------------------------------------------------------------------ Gitea

def admin_token():
    with open(TOKEN_FILE, encoding="utf-8") as f:
        token = f.read().strip()
    if not token:
        raise ApiError(HTTPStatus.SERVICE_UNAVAILABLE, "The server isn't set up yet.")
    return token


def gitea(method, path, body=None, auth=None):
    """Calls the Gitea API (as the admin unless auth is given). Returns (status, json-or-None)."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(f"{GITEA}/api/v1/{path}", data=data, method=method)
    request.add_header("Accept", "application/json")
    if data is not None:
        request.add_header("Content-Type", "application/json")
    request.add_header("Authorization", auth or f"token {admin_token()}")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            payload = json.loads(raw) if raw else None
        except ValueError:
            payload = {"message": raw.decode(errors="replace")}
        return e.code, payload
    except urllib.error.URLError as e:
        raise ApiError(HTTPStatus.BAD_GATEWAY, f"The vault server isn't answering ({e.reason}).")


def team_id(name):
    status, teams = gitea("GET", f"orgs/{ORG}/teams?limit=50")
    if status != 200:
        raise ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't list teams (HTTP {status}).")
    for team in teams:
        if team["name"] == name:
            return team["id"]
    raise ApiError(HTTPStatus.INTERNAL_SERVER_ERROR, f"Team {name} is missing; run setup.sh again.")


def org_members():
    members, page = [], 1
    while True:
        status, batch = gitea("GET", f"orgs/{ORG}/members?limit=50&page={page}")
        if status != 200:
            raise ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't list members (HTTP {status}).")
        members += batch
        if len(batch) < 50:
            return members
        page += 1


def require_member(handler):
    """The caller's own credentials (user + SwVault token or password), checked against Gitea."""
    key = handler.throttle_key("signin")
    check_throttle(key, "Too many failed sign-ins. Try again in 15 minutes.")
    auth = handler.headers.get("Authorization", "")
    if not auth.lower().startswith(("basic ", "token ")):
        raise ApiError(HTTPStatus.UNAUTHORIZED, "Sign in first.")
    status, user = gitea("GET", "user", auth=auth)
    if status != 200 or not user:
        record_failure(key)
        raise ApiError(HTTPStatus.UNAUTHORIZED, "Sign-in failed.")
    login = user["login"]
    status, _ = gitea("GET", f"orgs/{ORG}/members/{login}")
    if status not in (200, 204):
        raise ApiError(HTTPStatus.FORBIDDEN, f"{login} isn't on the {TEAM_NAME} team.")
    return login


def require_admin(handler):
    login = require_member(handler)
    status, _ = gitea("GET", f"teams/{team_id('Owners')}/members/{login}")
    if status not in (200, 204):
        raise ApiError(HTTPStatus.FORBIDDEN, f"{login} isn't a vault admin. Ask an admin to do this.")
    return login


# ------------------------------------------------------------------ handlers

def redeem(handler, body):
    ip = handler.client_ip()
    key = handler.throttle_key("redeem")
    check_throttle(key)
    code = body.get("code", "")
    username = (body.get("username") or "").strip()
    password = body.get("password") or ""
    full_name = (body.get("fullName") or "").strip()[:100]
    email = (body.get("email") or "").strip()
    member_type, subteam = clean_profile(body)

    with _lock:
        state = load_invites()
        invite = find_invite(state, code)
        if not invite or not is_active(invite):
            if key is not None:
                _failures.setdefault(key, []).append(time.time())  # _lock is held; no record_failure()
            raise ApiError(HTTPStatus.NOT_FOUND, "That invite code isn't valid (it may have expired or been used up). Ask your admin for a new one.")
        if not USERNAME_RE.match(username) or ".." in username:
            raise ApiError(HTTPStatus.BAD_REQUEST, "User names can use letters, digits and - . _ (up to 40 characters), and must start and end with a letter or digit.")
        if len(password) < 8:
            raise ApiError(HTTPStatus.BAD_REQUEST, "Choose a password of at least 8 characters.")
        if EMAIL_DOMAINS:
            if not email_domain_allowed(email):
                raise ApiError(HTTPStatus.BAD_REQUEST, f"Use your {domains_text()} email address.")
            check_verification(key, email, body.get("emailCode"), invite)
        else:
            email = ""

        status, payload = gitea("POST", "admin/users", {
            "username": username, "login_name": username, "email": email or f"{username}@noreply.localhost",
            "password": password, "full_name": full_name, "must_change_password": False,
            "send_notify": False, "visibility": "private",
        })
        text = json.dumps(payload or {}).lower()
        if status == 422 and "email" in text and ("already" in text or "used" in text):
            raise ApiError(HTTPStatus.CONFLICT, f"There's already an account for {email}. Sign in with it instead, or ask your admin to reset your password.")
        if status == 422 and "already exists" in text:
            raise ApiError(HTTPStatus.CONFLICT, f"The user name {username} is taken. Pick another one.")
        if status == 422:
            raise ApiError(HTTPStatus.BAD_REQUEST, "The server didn't accept that user name or password: " + (payload or {}).get("message", ""))
        if status not in (200, 201):
            raise ApiError(HTTPStatus.BAD_GATEWAY, f"Couldn't create the account (HTTP {status}).")

        team = ROLES.get(invite["role"], "Designers")
        status, _ = gitea("PUT", f"teams/{team_id(team)}/members/{username}")
        if status not in (200, 204):
            raise ApiError(HTTPStatus.BAD_GATEWAY, f"The account was created but couldn't be added to {team} (HTTP {status}). Ask your admin.")

        invite["usesLeft"] -= 1
        invite.setdefault("usedBy", []).append({"user": username, "email": email or None, "at": stamp()})
        save(INVITES_FILE, state)
        _verifications.pop(email.lower(), None)
    save_profile(username, member_type, subteam)
    log(f"invite {invite['code']} redeemed by {username} ({team}, {member_type}{' ' + subteam if subteam else ''}) from {ip}")
    return HTTPStatus.CREATED, {"username": username, "team": TEAM_NAME, "role": invite["role"]}


def describe_invite(code):
    invite = find_invite(load_invites(), code)
    valid = bool(invite and is_active(invite))
    result = {"team": TEAM_NAME, "valid": valid, "role": invite["role"] if valid else None, "emailDomains": EMAIL_DOMAINS}
    if valid:
        result["subteams"] = known_subteams(load_people())
    return HTTPStatus.OK, result


def list_people():
    profiles = load_people()["people"]
    people = []
    for member in org_members():
        profile = profiles.get(member["login"].lower(), {})
        people.append({
            "login": member["login"], "fullName": member.get("full_name") or "",
            "memberType": profile.get("memberType"), "subteam": profile.get("subteam") or "",
        })
    people.sort(key=lambda p: (p["memberType"] != "lead", (p["subteam"] or "").lower(), (p["fullName"] or p["login"]).lower()))
    return HTTPStatus.OK, {"people": people, "subteams": known_subteams({"people": profiles})}


def update_profile(login, body):
    member_type, subteam = clean_profile(body)
    save_profile(login, member_type, subteam)
    log(f"{login} is now {member_type}{' of ' + subteam if subteam else ''}")
    return HTTPStatus.OK, {"login": login, "memberType": member_type, "subteam": subteam}


# ------------------------------------------------------------------ review emails

REVIEW_HEADER_RE = re.compile(r"<!--\s*swvault-review\s+(\{.*?\})\s*-->", re.S)


def user_email(login):
    """The address to notify, or None (accounts added by the admin without --email have none)."""
    status, user = gitea("GET", f"users/{urllib.request.quote(login)}")
    email = ((user or {}).get("email") or "") if status == 200 else ""
    return None if not email or email.endswith("@noreply.localhost") else email


def display_name(login):
    status, user = gitea("GET", f"users/{urllib.request.quote(login)}")
    return ((user or {}).get("full_name") or login) if status == 200 else login


def notify_review(caller, number):
    """
    Called by SwVault right after a review request is opened or answered. The event is worked out
    from the issue itself (not from the caller), and each one is emailed once.
    """
    status, issue = gitea("GET", f"repos/{ORG}/{REPO}/issues/{number}")
    if status != 200 or not issue:
        raise ApiError(HTTPStatus.NOT_FOUND, "No such review request.")
    header = REVIEW_HEADER_RE.search(issue.get("body") or "")
    if not header:
        raise ApiError(HTTPStatus.BAD_REQUEST, "That isn't a SwVault review request.")
    info = json.loads(header.group(1))
    requester = (issue.get("user") or {}).get("login", "")
    leads = [a["login"] for a in issue.get("assignees") or []]
    if caller.lower() not in [requester.lower()] + [l.lower() for l in leads]:
        raise ApiError(HTTPStatus.FORBIDDEN, "Only the requester and the lead can do that.")
    labels = {l["name"] for l in issue.get("labels") or []}
    lead = leads[0] if leads else ""
    kind = {"simulation": "simulation", "drawing": "drawing"}.get(info.get("kind"), "design")
    path, version = info.get("path", ""), info.get("version", 0)
    file_name = path.rsplit("/", 1)[-1]
    web = issue.get("html_url") or ""
    how = "Open it in SOLIDWORKS: SwVault tab > Reviews (or the SwVault tray icon > Reviews)."

    if "review: cancelled" in labels:
        return HTTPStatus.OK, {"sent": False}
    def latest_from_lead(marker):
        """The lead's newest comment and its text after the SwVault marker (e.g. "Approved")."""
        _, comments = gitea("GET", f"repos/{ORG}/{REPO}/issues/{number}/comments")
        from_lead = [c for c in (comments or []) if (c.get("user") or {}).get("login", "").lower() == lead.lower()]
        latest = from_lead[-1] if from_lead else None
        return latest, re.sub(r"^.*?" + marker + r"\**\s*", "", (latest or {}).get("body", ""), count=1, flags=re.S).strip()

    if "review: approved" in labels:
        _, note = latest_from_lead("Approved")
        event, to_login = "approved", requester
        subject = f"Approved: {kind} review of {file_name}"
        text = (f"{display_name(lead)} approved your {kind} review request for {path} (version {version}).\n"
                + ("\n" + "\n".join("    " + line for line in note.splitlines()) + "\n" if note else ""))
    elif "review: changes requested" in labels:
        latest, feedback = latest_from_lead("Changes requested")
        event, to_login = f"changes:{latest['id'] if latest else 0}", requester
        subject = f"Changes requested: {kind} review of {file_name}"
        text = (f"{display_name(lead)} reviewed {path} (version {version}) and asked for changes:\n\n"
                + "\n".join("    " + line for line in (feedback or "(no details given)").splitlines()) + "\n\n"
                "Make the changes, check the file in, and request another review.\n")
    else:
        event, to_login = "opened", lead
        message = "\n".join(line[1:].strip() for line in (issue.get("body") or "").splitlines() if line.startswith(">"))
        subject = f"{display_name(requester)} asked you for a {kind} review of {file_name}"
        text = (f"{display_name(requester)} asked you, as a subteam lead, for a {kind} review of {path} (version {version}).\n"
                + (f"\nTheir note:\n" + "\n".join("    " + l for l in message.splitlines()) + "\n" if message else ""))

    key = f"{number}:{event}"
    with _lock:
        notified = load(NOTIFIED_FILE, {"sent": []})
        if key in notified["sent"]:
            return HTTPStatus.OK, {"sent": False, "reason": "already sent"}
    address = user_email(to_login) if to_login else None
    if not address:
        log(f"review #{number} {event}: {to_login or 'nobody'} has no email address; not emailed")
        return HTTPStatus.OK, {"sent": False, "reason": "no email address"}
    send_email(address, f"[{TEAM_NAME}] {subject}", text + f"\n{how}\n" + (f"On the web: {web}\n" if web else ""))
    with _lock:
        notified = load(NOTIFIED_FILE, {"sent": []})
        notified["sent"] = (notified["sent"] + [key])[-5000:]
        save(NOTIFIED_FILE, notified)
    log(f"review #{number} {event}: emailed {to_login}")
    return HTTPStatus.OK, {"sent": True, "to": to_login}


def create_invite(admin, body):
    role = (body.get("role") or "designer").lower()
    if role not in ROLES:
        raise ApiError(HTTPStatus.BAD_REQUEST, "role must be designer or viewer.")
    uses = int(body.get("uses") or 1)
    days = int(body.get("days") or 7)
    if not 1 <= uses <= 500 or not 1 <= days <= 90:
        raise ApiError(HTTPStatus.BAD_REQUEST, "uses must be 1-500 and days 1-90.")
    invite = {
        "code": new_code(), "role": role, "uses": uses, "usesLeft": uses,
        "expires": (now() + timedelta(days=days)).isoformat(timespec="seconds"),
        "note": (body.get("note") or "").strip()[:200], "createdBy": admin, "createdAt": stamp(),
    }
    with _lock:
        state = load_invites()
        # Drop long-dead invites so the file doesn't grow forever.
        cutoff = now() - timedelta(days=180)
        state["invites"] = [i for i in state["invites"] if datetime.fromisoformat(i["expires"]) > cutoff]
        state["invites"].append(invite)
        save(INVITES_FILE, state)
    log(f"invite {invite['code']} created by {admin}: {role}, {uses} use(s), {days} day(s)")
    return HTTPStatus.CREATED, public_invite(invite)


def list_invites():
    return HTTPStatus.OK, [public_invite(i) for i in load_invites()["invites"] if is_active(i)]


def revoke_invite(admin, code):
    with _lock:
        state = load_invites()
        invite = find_invite(state, code)
        if not invite:
            raise ApiError(HTTPStatus.NOT_FOUND, "No such invite.")
        invite["usesLeft"] = 0
        save(INVITES_FILE, state)
    log(f"invite {invite['code']} revoked by {admin}")
    return HTTPStatus.OK, public_invite(invite)


def test_email(admin, body):
    to = (body.get("to") or "").strip()
    if not EMAIL_RE.match(to):
        raise ApiError(HTTPStatus.BAD_REQUEST, "Give an email address to send the test to.")
    send_email(to, f"SwVault {TEAM_NAME}: test email", f"Email from your SwVault server works. (Sent by {admin}.)\n")
    return HTTPStatus.OK, {"sent": True}


class Handler(BaseHTTPRequestHandler):
    server_version = "SwVaultTeam/1"

    def log_message(self, fmt, *args):
        pass  # requests are logged by the handlers that matter

    def client_ip(self):
        # The proxy (Tailscale) appends the real client address; earlier entries come from the client.
        forwarded = self.headers.get("X-Forwarded-For", "")
        return forwarded.split(",")[-1].strip() or self.client_address[0]

    def throttle_key(self, bucket):
        # Internet traffic can only arrive through Tailscale Funnel, which adds X-Forwarded-For.
        # Without it, the request came from this machine (swvault-admin.sh via 127.0.0.1:3100).
        if not self.headers.get("X-Forwarded-For"):
            return None
        return f"{bucket}:{self.client_ip()}"

    def route(self):
        path = self.path.split("?", 1)[0]
        if path.startswith(PREFIX):
            path = path[len(PREFIX):]  # Tailscale may or may not strip the mount point
        return "/" + urllib.request.unquote(path).strip("/")

    def query(self, name):
        if "?" not in self.path:
            return None
        for part in self.path.split("?", 1)[1].split("&"):
            key, _, value = part.partition("=")
            if key == name:
                return urllib.request.unquote(value)
        return None

    def read_json(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length > 64 * 1024:
            raise ApiError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "Request too large.")
        raw = self.rfile.read(length) if length else b"{}"
        try:
            body = json.loads(raw or b"{}")
        except ValueError:
            raise ApiError(HTTPStatus.BAD_REQUEST, "Expected JSON.")
        if not isinstance(body, dict):
            raise ApiError(HTTPStatus.BAD_REQUEST, "Expected a JSON object.")
        return body

    def send_json(self, status, payload):
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def handle_request(self, method):
        try:
            route = self.route()
            if method == "GET" and route == "/health":
                result = (HTTPStatus.OK, {"ok": True, "emailDomains": EMAIL_DOMAINS, "emailConfigured": bool(SMTP_HOST)})
            elif method == "GET" and route.startswith("/invite/"):
                result = describe_invite(route[len("/invite/"):])
            elif method == "POST" and route == "/verify":
                result = start_verification(self, self.read_json())
            elif method == "POST" and route == "/redeem":
                result = redeem(self, self.read_json())
            elif method == "GET" and route == "/download":
                return self.download()
            elif method == "GET" and route == "/people":
                require_member(self)
                result = list_people()
            elif method == "PUT" and route == "/people/me":
                result = update_profile(require_member(self), self.read_json())
            elif method == "PUT" and route.startswith("/people/"):
                require_admin(self)  # admins can set anyone's profile (e.g. mark the subteam leads)
                result = update_profile(route[len("/people/"):], self.read_json())
            elif method == "POST" and re.fullmatch(r"/reviews/\d+/notify", route):
                result = notify_review(require_member(self), int(route.split("/")[2]))
            elif method == "POST" and route == "/invites":
                result = create_invite(require_admin(self), self.read_json())
            elif method == "GET" and route == "/invites":
                require_admin(self)
                result = list_invites()
            elif method == "DELETE" and route.startswith("/invites/"):
                result = revoke_invite(require_admin(self), route[len("/invites/"):])
            elif method == "PUT" and route == "/installer":
                return self.upload(require_admin(self))
            elif method == "POST" and route == "/test-email":
                result = test_email(require_admin(self), self.read_json())
            else:
                result = (HTTPStatus.NOT_FOUND, {"error": "Not found."})
            self.send_json(*result)
        except ApiError as e:
            self.send_json(e.status, {"error": e.message})
        except Exception as e:  # never leak a stack trace to the internet
            log(f"error handling {method} {self.path}: {e!r}")
            self.send_json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": "Internal error in the SwVault team service."})

    def download(self):
        if not os.path.exists(INSTALLER_FILE):
            raise ApiError(HTTPStatus.NOT_FOUND, "Your admin hasn't published the installer yet.")
        code = normalize_code(self.query("invite") or "")
        safe_team = re.sub(r"[^A-Za-z0-9_-]", "", TEAM_NAME) or "Team"
        # The installer reads the invite code from its folder name ("Extract All" names the folder
        # after the zip), so the member doesn't have to type it.
        name = f"SwVault-{safe_team}" + (f"-invite-{code[0:4]}-{code[4:8]}-{code[8:12]}" if len(code) == 12 else "") + ".zip"
        size = os.path.getsize(INSTALLER_FILE)
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "application/zip")
        self.send_header("Content-Length", str(size))
        self.send_header("Content-Disposition", f'attachment; filename="{name}"')
        self.end_headers()
        with open(INSTALLER_FILE, "rb") as f:
            shutil.copyfileobj(f, self.wfile, 1024 * 1024)

    def upload(self, admin):
        length = int(self.headers.get("Content-Length") or 0)
        if not 0 < length <= MAX_UPLOAD:
            raise ApiError(HTTPStatus.BAD_REQUEST, "Send the installer zip as the request body.")
        os.makedirs(os.path.dirname(INSTALLER_FILE), exist_ok=True)
        tmp = INSTALLER_FILE + ".upload"
        remaining = length
        with open(tmp, "wb") as f:
            while remaining > 0:
                chunk = self.rfile.read(min(remaining, 1024 * 1024))
                if not chunk:
                    raise ApiError(HTTPStatus.BAD_REQUEST, "Upload was cut off.")
                f.write(chunk)
                remaining -= len(chunk)
        with open(tmp, "rb") as f:
            if f.read(2) != b"PK":
                os.remove(tmp)
                raise ApiError(HTTPStatus.BAD_REQUEST, "That isn't a zip file.")
        os.replace(tmp, INSTALLER_FILE)
        log(f"installer published by {admin} ({length} bytes)")
        self.send_json(HTTPStatus.OK, {"size": length})

    def do_GET(self):
        self.handle_request("GET")

    def do_POST(self):
        self.handle_request("POST")

    def do_PUT(self):
        self.handle_request("PUT")

    def do_DELETE(self):
        self.handle_request("DELETE")


def main():
    os.makedirs(STATE_DIR, exist_ok=True)
    server = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    email = f"members verify {domains_text()} addresses" + ("" if SMTP_HOST else " (but SMTP isn't set up!)") if EMAIL_DOMAINS else "no email check"
    log(f"SwVault team service for {TEAM_NAME} ({ORG}) on port {PORT}; {email}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        sys.exit(0)


if __name__ == "__main__":
    main()
