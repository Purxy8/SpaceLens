namespace DesktopOrganizer;

internal sealed class ProtectedRecycleDialog : Form
{
    private readonly TextBox confirmation = new() { Dock = DockStyle.Top, Height = 32 };
    private readonly Button continueButton = new() { Text = "Recycle protected files", DialogResult = DialogResult.OK, Enabled = false, AutoSize = true, Padding = new Padding(12, 5, 12, 5) };
    internal string RequiredPhrase { get; }

    internal ProtectedRecycleDialog(IReadOnlyList<FileItem> protectedItems)
    {
        ArgumentNullException.ThrowIfNull(protectedItems);
        if (protectedItems.Count == 0) throw new ArgumentException("At least one protected file is required.", nameof(protectedItems));

        RequiredPhrase = protectedItems.Count == 1 ? "RECYCLE PROTECTED FILE" : $"RECYCLE {protectedItems.Count} PROTECTED FILES";
        Text = "Protected files — explicit confirmation";
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(620, 440);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(247, 249, 252);

        var warning = new Label
        {
            Dock = DockStyle.Top,
            Height = 92,
            Padding = new Padding(16, 14, 16, 8),
            BackColor = Color.FromArgb(255, 235, 232),
            ForeColor = Color.FromArgb(145, 35, 30),
            Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold),
            Text = $"DANGER: {protectedItems.Count:N0} protected Windows/system file{(protectedItems.Count == 1 ? " is" : "s are")} selected. Recycling these files can stop Windows from booting, updating, recovering, or working correctly. Windows may refuse the operation."
        };

        var paths = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false, BackColor = Color.White };
        paths.Items.AddRange(protectedItems.Select(item => item.Path).Cast<object>().ToArray());

        var instruction = new Label
        {
            Dock = DockStyle.Top,
            Height = 55,
            Padding = new Padding(0, 8, 0, 6),
            Text = $"To confirm that you understand the risk, type exactly:\n{RequiredPhrase}",
            ForeColor = Color.FromArgb(55, 65, 81)
        };

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(12, 5, 12, 5) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8), BackColor = Color.FromArgb(247, 249, 252) };
        actions.Controls.Add(continueButton);
        actions.Controls.Add(cancel);

        var confirmationPanel = new Panel { Dock = DockStyle.Bottom, Height = 96, Padding = new Padding(16, 0, 16, 8), BackColor = Color.FromArgb(247, 249, 252) };
        confirmationPanel.Controls.Add(confirmation);
        confirmationPanel.Controls.Add(instruction);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 10) };
        content.Controls.Add(paths);

        Controls.Add(content);
        Controls.Add(confirmationPanel);
        Controls.Add(actions);
        Controls.Add(warning);

        confirmation.TextChanged += (_, _) => continueButton.Enabled = string.Equals(confirmation.Text.Trim(), RequiredPhrase, StringComparison.Ordinal);
        Shown += (_, _) => confirmation.Focus();
        AcceptButton = continueButton;
        CancelButton = cancel;
    }

    internal static bool Confirm(IWin32Window owner, IReadOnlyList<FileItem> protectedItems)
    {
        using var dialog = new ProtectedRecycleDialog(protectedItems);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
