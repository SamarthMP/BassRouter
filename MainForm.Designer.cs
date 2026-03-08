namespace BassRouter;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    // Primary output controls
    private Label LabelPrimaryHeader;
    private Label LabelPrimaryDevice;
    private ComboBox ComboPrimaryDevice;
    private Label LabelPrimaryLatency;
    private TrackBar SliderPrimaryLatency;
    private Label LabelPrimaryLatencyValue;
    private Label LabelPrimaryVolume;
    private TrackBar SliderPrimaryVolume;
    private Label LabelPrimaryVolumeValue;

    // Subwoofer output controls
    private Label LabelSubHeader;
    private Label LabelSubDevice;
    private ComboBox ComboSubDevice;
    private Label LabelSubLatency;
    private TrackBar SliderSubLatency;
    private Label LabelSubLatencyValue;
    private Label LabelSubVolume;
    private TrackBar SliderSubVolume;
    private Label LabelSubVolumeValue;
    private Label LabelLowPass;
    private TrackBar SliderLowPass;
    private Label LabelLowPassValue;

    // Bottom controls
    private Panel PanelBottom;
    private Label LabelStatus;
    private Button ButtonStartStop;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        SuspendLayout();

        // ── Form ──
        Text = "BassRouter";
        ClientSize = new Size(520, 520);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(220, 220, 220);

        int leftMargin = 20;
        int controlLeft = 20;
        int sliderWidth = 340;
        int sliderRight = controlLeft + sliderWidth;
        int valueLabelLeft = sliderRight + 10;
        int y = 15;

        // ══════════════════════════════════════════════════
        //  PRIMARY AUDIO OUTPUT
        // ══════════════════════════════════════════════════

        LabelPrimaryHeader = CreateHeaderLabel("Primary Audio Output", leftMargin, y);
        Controls.Add(LabelPrimaryHeader);
        y += 28;

        LabelPrimaryDevice = CreateLabel("Device:", leftMargin, y + 3);
        Controls.Add(LabelPrimaryDevice);

        ComboPrimaryDevice = new ComboBox
        {
            Location = new Point(leftMargin + 60, y),
            Size = new Size(420, 25),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = Color.FromArgb(220, 220, 220),
            FlatStyle = FlatStyle.Flat
        };
        Controls.Add(ComboPrimaryDevice);
        y += 35;

        LabelPrimaryLatency = CreateLabel("Latency:", leftMargin, y + 3);
        Controls.Add(LabelPrimaryLatency);

        SliderPrimaryLatency = CreateSlider(controlLeft + 60, y, sliderWidth - 60, 0, 5000, 0, 100);
        Controls.Add(SliderPrimaryLatency);

        LabelPrimaryLatencyValue = CreateValueLabel(valueLabelLeft, y + 3, "0 ms");
        Controls.Add(LabelPrimaryLatencyValue);
        y += 40;

        LabelPrimaryVolume = CreateLabel("Volume:", leftMargin, y + 3);
        Controls.Add(LabelPrimaryVolume);

        SliderPrimaryVolume = CreateSlider(controlLeft + 60, y, sliderWidth - 60, 0, 100, 100, 1);
        Controls.Add(SliderPrimaryVolume);

        LabelPrimaryVolumeValue = CreateValueLabel(valueLabelLeft, y + 3, "100%");
        Controls.Add(LabelPrimaryVolumeValue);
        y += 50;

        // ══════════════════════════════════════════════════
        //  SEPARATOR
        // ══════════════════════════════════════════════════

        var separator = new Panel
        {
            Location = new Point(leftMargin, y),
            Size = new Size(480, 1),
            BackColor = Color.FromArgb(70, 70, 70)
        };
        Controls.Add(separator);
        y += 15;

        // ══════════════════════════════════════════════════
        //  SUBWOOFER AUDIO OUTPUT
        // ══════════════════════════════════════════════════

        LabelSubHeader = CreateHeaderLabel("Subwoofer Audio Output", leftMargin, y);
        Controls.Add(LabelSubHeader);
        y += 28;

        LabelSubDevice = CreateLabel("Device:", leftMargin, y + 3);
        Controls.Add(LabelSubDevice);

        ComboSubDevice = new ComboBox
        {
            Location = new Point(leftMargin + 60, y),
            Size = new Size(420, 25),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = Color.FromArgb(220, 220, 220),
            FlatStyle = FlatStyle.Flat
        };
        Controls.Add(ComboSubDevice);
        y += 35;

        LabelSubLatency = CreateLabel("Latency:", leftMargin, y + 3);
        Controls.Add(LabelSubLatency);

        SliderSubLatency = CreateSlider(controlLeft + 60, y, sliderWidth - 60, 0, 5000, 0, 100);
        Controls.Add(SliderSubLatency);

        LabelSubLatencyValue = CreateValueLabel(valueLabelLeft, y + 3, "0 ms");
        Controls.Add(LabelSubLatencyValue);
        y += 40;

        LabelSubVolume = CreateLabel("Volume:", leftMargin, y + 3);
        Controls.Add(LabelSubVolume);

        SliderSubVolume = CreateSlider(controlLeft + 60, y, sliderWidth - 60, 0, 100, 100, 1);
        Controls.Add(SliderSubVolume);

        LabelSubVolumeValue = CreateValueLabel(valueLabelLeft, y + 3, "100%");
        Controls.Add(LabelSubVolumeValue);
        y += 40;

        LabelLowPass = CreateLabel("Low Pass:", leftMargin, y + 3);
        Controls.Add(LabelLowPass);

        SliderLowPass = CreateSlider(controlLeft + 60, y, sliderWidth - 60, 10, 300, 120, 1);
        Controls.Add(SliderLowPass);

        LabelLowPassValue = CreateValueLabel(valueLabelLeft, y + 3, "120 Hz");
        Controls.Add(LabelLowPassValue);
        y += 55;

        // ══════════════════════════════════════════════════
        //  BOTTOM BAR
        // ══════════════════════════════════════════════════

        PanelBottom = new Panel
        {
            Location = new Point(0, y),
            Size = new Size(520, 50),
            BackColor = Color.FromArgb(25, 25, 25)
        };

        LabelStatus = new Label
        {
            Text = "Stopped",
            Location = new Point(20, 15),
            Size = new Size(340, 20),
            ForeColor = Color.FromArgb(180, 180, 180),
            Font = new Font("Segoe UI", 9F)
        };
        PanelBottom.Controls.Add(LabelStatus);

        ButtonStartStop = new Button
        {
            Text = "Start",
            Location = new Point(390, 10),
            Size = new Size(110, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 60),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        ButtonStartStop.FlatAppearance.BorderSize = 0;
        PanelBottom.Controls.Add(ButtonStartStop);

        Controls.Add(PanelBottom);

        // Adjust form height
        ClientSize = new Size(520, y + 50);

        ResumeLayout(false);
    }

    private static Label CreateHeaderLabel(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 180, 255)
        };
    }

    private static Label CreateLabel(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(200, 200, 200)
        };
    }

    private static Label CreateValueLabel(int x, int y, string text)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(80, 20),
            ForeColor = Color.FromArgb(255, 200, 80),
            TextAlign = ContentAlignment.MiddleRight
        };
    }

    private static TrackBar CreateSlider(int x, int y, int width, int min, int max, int value, int smallChange)
    {
        return new TrackBar
        {
            Location = new Point(x, y),
            Size = new Size(width, 30),
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            SmallChange = smallChange,
            LargeChange = smallChange * 10,
            TickStyle = TickStyle.None,
            BackColor = Color.FromArgb(30, 30, 30)
        };
    }
}
