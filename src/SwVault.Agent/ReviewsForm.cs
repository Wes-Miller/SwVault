using System.Diagnostics;
using SwVault.Core;
using SwVault.Core.Client;

namespace SwVault.Agent;

/// <summary>
/// Review inbox. "For me": requests sent to me as a lead (open the file, approve, ask for changes,
/// comment). "My requests": what I asked for and what the leads said (comment, cancel).
/// </summary>
internal sealed class ReviewsForm : Form
{
    private readonly AgentHost _agent;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _forMe = NewList("From");
    private readonly ListView _mine = NewList("Lead");
    private readonly CheckBox _showFinished = new() { Text = "Show finished", AutoSize = true };
    private readonly Label _heading = new() { AutoSize = true, MaximumSize = new Size(700, 0), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly TextBox _history = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox _comment = new() { Multiline = true, Height = 54, Dock = DockStyle.Fill, PlaceholderText = "Comment (optional for Approve)" };
    private readonly Button _open = new() { Text = "Open file", AutoSize = true };
    private readonly Button _approve = new() { Text = "Approve", AutoSize = true };
    private readonly Button _changes = new() { Text = "Request changes", AutoSize = true };
    private readonly Button _send = new() { Text = "Comment", AutoSize = true };
    private readonly Button _cancelRequest = new() { Text = "Cancel request", AutoSize = true };
    private readonly Button _web = new() { Text = "Open on the web", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };
    private ReviewRequest? _selected;
    private bool _selectedIsForMe;

    public ReviewsForm(AgentHost agent, bool startOnMine = false)
    {
        _agent = agent;
        Text = "SwVault - Reviews";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 780;
        Height = 640;
        MinimumSize = new Size(640, 520);
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;

        var forMePage = new TabPage("For me (as a lead)");
        forMePage.Controls.Add(_forMe);
        var minePage = new TabPage("My requests");
        minePage.Controls.Add(_mine);
        _tabs.TabPages.Add(forMePage);
        _tabs.TabPages.Add(minePage);
        if (startOnMine) _tabs.SelectedIndex = 1;

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
        var top = new Panel { Dock = DockStyle.Fill };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 0) };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var role = new Button { Text = "My team role...", AutoSize = true };
        role.Click += (_, _) => { using var f = new ProfileForm(_agent); f.ShowDialog(this); };
        bar.Controls.Add(refresh);
        bar.Controls.Add(role);
        bar.Controls.Add(_showFinished);
        bar.Controls.Add(_status);
        top.Controls.Add(_tabs);
        top.Controls.Add(bar);
        split.Panel1.Controls.Add(top);

        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.Controls.Add(_heading);
        detail.Controls.Add(_history);
        detail.Controls.Add(_comment);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.AddRange(new Control[] { _open, _approve, _changes, _send, _cancelRequest, _web });
        detail.Controls.Add(actions);
        split.Panel2.Controls.Add(detail);
        Controls.Add(split);

        refresh.Click += async (_, _) => await LoadAsync();
        _showFinished.CheckedChanged += async (_, _) => await LoadAsync();
        _forMe.SelectedIndexChanged += async (_, _) => await SelectAsync(_forMe, forMe: true);
        _mine.SelectedIndexChanged += async (_, _) => await SelectAsync(_mine, forMe: false);
        _tabs.SelectedIndexChanged += async (_, _) => await SelectAsync(_tabs.SelectedIndex == 0 ? _forMe : _mine, _tabs.SelectedIndex == 0);
        _open.Click += (_, _) => OpenFile();
        _web.Click += (_, _) => { if (!string.IsNullOrEmpty(_selected?.WebUrl)) Process.Start(new ProcessStartInfo(_selected.WebUrl) { UseShellExecute = true }); };
        _approve.Click += async (_, _) => await RespondAsync(ReviewDecision.Approve);
        _changes.Click += async (_, _) => await RespondAsync(ReviewDecision.RequestChanges);
        _send.Click += async (_, _) => await RespondAsync(ReviewDecision.Comment);
        _cancelRequest.Click += async (_, _) => await RespondAsync(ReviewDecision.Cancel);
        _agent.ReviewWatcher.Changed += OnWatcherChanged;
        FormClosed += (_, _) => _agent.ReviewWatcher.Changed -= OnWatcherChanged;
        Shown += async (_, _) => await LoadAsync();
        ShowDetail(null, false, Array.Empty<ReviewComment>());
    }

    private static ListView NewList(string personColumn)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        list.Columns.Add("Review", 130);
        list.Columns.Add("File", 220);
        list.Columns.Add("Ver", 45);
        list.Columns.Add(personColumn, 110);
        list.Columns.Add("Status", 140);
        list.Columns.Add("Updated", 90);
        return list;
    }

    private void OnWatcherChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(async () => await LoadAsync());
    }

    private async Task LoadAsync()
    {
        _status.Text = "Loading...";
        try
        {
            var finished = _showFinished.Checked;
            var forMe = await Task.Run(() => _agent.ListReviewsAsync(forMe: true, includeClosed: finished));
            var mine = await Task.Run(() => _agent.ListReviewsAsync(forMe: false, includeClosed: true));
            Fill(_forMe, forMe, r => r.Requester);
            Fill(_mine, finished ? mine : mine.Where(r => r.Status is ReviewStatus.Waiting or ReviewStatus.ChangesRequested || r.Updated > DateTimeOffset.Now.AddDays(-14)).ToList(), r => r.Lead);
            var waiting = forMe.Count(r => r.Status == ReviewStatus.Waiting);
            _tabs.TabPages[0].Text = waiting > 0 ? $"For me (as a lead) - {waiting} waiting" : "For me (as a lead)";
            _status.Text = "";
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private static void Fill(ListView list, IReadOnlyList<ReviewRequest> reviews, Func<ReviewRequest, string> person)
    {
        var selected = list.SelectedItems.Count > 0 ? ((ReviewRequest)list.SelectedItems[0].Tag!).Number : -1;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var r in reviews)
        {
            var item = new ListViewItem(new[] { r.KindText, r.FileName, "v" + r.Version, person(r), r.StatusText, r.Updated.ToLocalTime().ToString("MMM d") }) { Tag = r };
            if (r.Status == ReviewStatus.Waiting) item.Font = new Font(list.Font, FontStyle.Bold);
            if (r.Status == ReviewStatus.ChangesRequested) item.ForeColor = Color.DarkGoldenrod;
            if (r.Status is ReviewStatus.Approved or ReviewStatus.Cancelled) item.ForeColor = Color.DimGray;
            list.Items.Add(item);
            if (r.Number == selected) item.Selected = true;
        }
        list.EndUpdate();
    }

    private async Task SelectAsync(ListView list, bool forMe)
    {
        var review = list.SelectedItems.Count > 0 ? (ReviewRequest)list.SelectedItems[0].Tag! : null;
        if (review == null)
        {
            ShowDetail(null, forMe, Array.Empty<ReviewComment>());
            return;
        }
        ShowDetail(review, forMe, Array.Empty<ReviewComment>());
        try
        {
            var comments = await Task.Run(() => _agent.ReviewCommentsAsync(review));
            if (_selected?.Number == review.Number) ShowDetail(review, forMe, comments);
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void ShowDetail(ReviewRequest? review, bool forMe, IReadOnlyList<ReviewComment> comments)
    {
        _selected = review;
        _selectedIsForMe = forMe;
        var open = review != null && review.Status is ReviewStatus.Waiting or ReviewStatus.ChangesRequested;
        _approve.Visible = _changes.Visible = forMe;
        _cancelRequest.Visible = !forMe;
        _approve.Enabled = _changes.Enabled = _cancelRequest.Enabled = open;
        _open.Enabled = _web.Enabled = _send.Enabled = _comment.Enabled = review != null;
        if (review == null)
        {
            _heading.Text = "Select a review.";
            _history.Text = "";
            return;
        }
        _heading.Text = $"{review.KindText} of {review.Path} (version {review.Version})\r\n" +
                        $"Requested by {review.Requester} from {review.Lead} on {review.Created.ToLocalTime():MMM d} - {review.StatusText}";
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(review.Message)) lines.Add($"{review.Requester}: {review.Message}");
        lines.AddRange(comments.Select(c => $"{c.Author} ({c.At.ToLocalTime():MMM d, h:mm tt}): {c.Text}"));
        _history.Text = lines.Count == 0 ? "(no messages yet)" : string.Join("\r\n\r\n", lines).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
    }

    private void OpenFile()
    {
        if (_selected == null) return;
        var local = _agent.LocalPathOf(_selected.Path);
        if (local == null || !File.Exists(local))
        {
            _status.Text = "That file isn't on this PC yet. Use Get Latest in SOLIDWORKS (SwVault task pane), then try again.";
            return;
        }
        Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        _status.Text = $"Opening {_selected.FileName}. If it isn't the latest version, use Get Latest in SOLIDWORKS.";
    }

    private async Task RespondAsync(ReviewDecision decision)
    {
        var review = _selected;
        if (review == null) return;
        var comment = _comment.Text.Trim();
        if (decision is ReviewDecision.Comment or ReviewDecision.RequestChanges && comment.Length == 0)
        {
            _status.Text = decision == ReviewDecision.RequestChanges ? "Say what should change in the comment box." : "Write a comment first.";
            _comment.Focus();
            return;
        }
        if (decision == ReviewDecision.Cancel && MessageBox.Show(this, $"Cancel your review request for {review.FileName}?", "SwVault", MessageBoxButtons.OKCancel) != DialogResult.OK)
            return;
        _status.Text = "Sending...";
        try
        {
            await Task.Run(() => _agent.RespondToReviewAsync(review, decision, comment));
            _comment.Clear();
            _status.Text = decision switch
            {
                ReviewDecision.Approve => $"Approved. {review.Requester} will be told.",
                ReviewDecision.RequestChanges => $"Sent. {review.Requester} will be told.",
                ReviewDecision.Cancel => "Request cancelled.",
                _ => "Comment sent.",
            };
            await LoadAsync();
            await SelectAsync(_selectedIsForMe ? _forMe : _mine, _selectedIsForMe);
        }
        catch (VaultException ex)
        {
            _status.Text = ex.Message;
        }
    }
}
