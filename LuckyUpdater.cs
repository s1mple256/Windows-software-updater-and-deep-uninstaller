using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Globalization;
using Microsoft.Win32;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace LuckyUpdater
{
    internal static class PackageIdPolicy
    {
        private static readonly Regex WingetIdPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._+-]{1,}$", RegexOptions.Compiled);
        // Keep the historical installed-list ID formats (including ARP paths and colon IDs), adding '+' rather than narrowing them.
        private static readonly Regex LegacyInstalledIdPattern = new Regex(@"^[A-Za-z0-9{][A-Za-z0-9._{}\\:+-]*$", RegexOptions.Compiled);

        internal static bool IsWingetId(string id) { return !String.IsNullOrEmpty(id) && WingetIdPattern.IsMatch(id); }
        internal static bool IsSafeId(string id) { return IsWingetId(id) || (!String.IsNullOrEmpty(id) && LegacyInstalledIdPattern.IsMatch(id)); }
    }

    internal static class ProtectedPathPolicy
    {
        private static bool configurationFailed;
        private static readonly string[] paths = LoadPaths();
        internal static string[] Paths { get { return (string[])paths.Clone(); } }
        internal static bool ConfigurationFailed { get { return configurationFailed; } }

        private static string[] LoadPaths()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (String.IsNullOrWhiteSpace(local)) return new string[0];
                string config = Path.Combine(local, "LuckyUpdater", "protected-paths.txt");
                if (!File.Exists(config)) return new string[0];
                var result = new List<string>();
                foreach (string raw in File.ReadAllLines(config, Encoding.UTF8))
                {
                    string entry = (raw ?? "").Trim();
                    if (entry.Length == 0 || entry.StartsWith("#", StringComparison.Ordinal)) continue;
                    string normalized;
                    if (!TryNormalize(entry, out normalized)) { configurationFailed = true; return new string[0]; }
                    if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) result.Add(normalized);
                }
                return result.ToArray();
            }
            catch { configurationFailed = true; return new string[0]; }
        }

        internal static bool IsProtected(string candidate)
        {
            if (configurationFailed) return true;
            return IsProtected(candidate, paths);
        }

        internal static bool IsProtected(string candidate, IEnumerable<string> roots)
        {
            string full;
            if (!TryNormalize(candidate, out full)) return true;
            foreach (string root in roots ?? Enumerable.Empty<string>())
            {
                string safe;
                if (!TryNormalize(root, out safe)) return true;
                if (SameOrBelow(full, safe) || SameOrBelow(safe, full)) return true;
            }
            return false;
        }

        private static bool TryNormalize(string value, out string normalized)
        {
            normalized = "";
            try
            {
                if (String.IsNullOrWhiteSpace(value)) return false;
                string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                string full = Path.GetFullPath(expanded);
                string root = Path.GetPathRoot(full) ?? "";
                while (full.Length > root.Length && (full.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) || full.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)))
                    full = full.Substring(0, full.Length - 1);
                normalized = full;
                return true;
            }
            catch { return false; }
        }

        private static bool SameOrBelow(string path, string root)
        {
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
    internal sealed class UpdateItem
    {
        public string Name;
        public string Id;
        public string CurrentVersion;
        public string AvailableVersion;
        public string Source;
        public Panel Row;
        public Button UpdateButton;
        public PictureBox IconBox;
    }

    internal sealed class InstalledPackage
    {
        public string Name;
        public string Id;
        public string Version;
        public string Source;
        public Panel Row;
        public Button UninstallButton;
        public PictureBox IconBox;
        public string QuietUninstallString;
        public string UninstallString;
        public List<string> UninstallLocations = new List<string>();
    }

    internal sealed class CommandResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }

    internal sealed class CleanupCandidate
    {
        public string Category;
        public string Path;
        public string Label;
        public long Size;
        public bool IsFile;
        public bool IsUserData;
        public bool CheckedByDefault;
        public bool SizeIsEstimate;
        public string ActionKind;
        public string Evidence;
        public string RegistryHive;
        public string RegistryKeyPath;
        public string RegistryValueName;
        public int ProcessId;
        public string ServiceName;
        public bool IsHighRisk;
        public string RegistryValueData;
        public int Tier;
    }

    internal sealed class UninstallerPlan
    {
        public string Executable;
        public string Arguments;
        public bool UseWinget;
        public bool IsQuiet;
        public string Description;
    }

    internal sealed class UninstallJob
    {
        public InstalledPackage Package;
        public List<CleanupCandidate> Cleanup;
        public List<CleanupCandidate> AllCandidates;
        public UninstallerPlan Uninstaller;
    }

    internal sealed class StartupData : IDisposable
    {
        public CommandResult UpdateScan;
        public CommandResult InstalledScan;
        public List<UpdateItem> Updates = new List<UpdateItem>();
        public List<InstalledPackage> InstalledPackages = new List<InstalledPackage>();
        public bool IconsLoaded;
        public readonly Dictionary<string, Image> IconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        public event Action<string, Image> IconUpdated;

        internal static string IconKey(string id) { return id ?? ""; }

        internal void SetIcon(string id, Image image)
        {
            if (image == null) return;
            string key = IconKey(id);
            Image stored = new Bitmap(image);
            Image old;
            if (IconCache.TryGetValue(key, out old)) old.Dispose();
            IconCache[key] = stored;
            if (IconUpdated != null) IconUpdated(key, stored);
        }

        public void Dispose()
        {
            foreach (Image image in IconCache.Values) image.Dispose();
            IconCache.Clear();
        }
    }

    internal sealed class StartupSplash : Form
    {
        private readonly BackgroundWorker worker;
        private readonly Timer timer;
        private readonly LoadingVisual visual;
        private int animationFrame;
        private int targetProgress;
        private bool loadingComplete;
        private readonly Stopwatch loadingClock = new Stopwatch();
        private Timer closeTimer;
        public StartupData Data { get; private set; }
        public Action<StartupData> PrepareMainWindow { get; set; }

        public StartupSplash()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1060, 760);
            ShowInTaskbar = true;
            TopMost = true;
            DoubleBuffered = true;
            BackgroundImage = VisualTheme.LoadLandscape();
            BackgroundImageLayout = ImageLayout.Stretch;
            BackColor = Color.White;
            visual = new LoadingVisual { Dock = DockStyle.Fill };
            Controls.Add(visual);
            worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.DoWork += LoadData;
            worker.ProgressChanged += delegate(object sender, ProgressChangedEventArgs e)
            {
                targetProgress = Math.Max(targetProgress, Math.Max(0, Math.Min(99, e.ProgressPercentage)));
                visual.Stage = Convert.ToString(e.UserState);
                visual.Invalidate();
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                if (e.Error != null)
                {
                    Data = new StartupData
                    {
                        UpdateScan = new CommandResult { ExitCode = 1, Error = e.Error.Message, Output = "" },
                        InstalledScan = new CommandResult { ExitCode = 1, Error = e.Error.Message, Output = "" },
                        IconsLoaded = true
                    };
                    visual.Stage = "已完成可用项目的准备，部分清单读取失败";
                }
                else Data = (StartupData)e.Result;
                if (PrepareMainWindow != null && Data != null)
                {
                    try { PrepareMainWindow(Data); }
                    catch (Exception ex) { visual.Stage = "主界面预载失败：" + ex.Message; }
                }
                targetProgress = 100;
                loadingComplete = true;
                visual.Invalidate();
            };
            timer = new Timer { Interval = 80 };
            timer.Tick += delegate
            {
                animationFrame++;
                visual.Frame = animationFrame;
                int slowCrawl = Math.Min(90, (int)(loadingClock.ElapsedMilliseconds / 1000));
                int desired = Math.Max(targetProgress, slowCrawl);
                if (loadingComplete) desired = 100;
                if (visual.Progress < desired) visual.Progress = Math.Min(desired, visual.Progress + 3);
                visual.Invalidate();
                if (loadingComplete && visual.Progress >= 100)
                {
                    timer.Stop();
                    if (closeTimer == null)
                    {
                        closeTimer = new Timer { Interval = 260 };
                        closeTimer.Tick += delegate
                        {
                            closeTimer.Stop();
                            closeTimer.Dispose();
                            closeTimer = null;
                            DialogResult = DialogResult.OK;
                            Close();
                        };
                        closeTimer.Start();
                    }
                }
            };
            FormClosed += delegate
            {
                timer.Stop();
                if (closeTimer != null) { closeTimer.Stop(); closeTimer.Dispose(); closeTimer = null; }
            };
            Shown += delegate
            {
                loadingClock.Start();
                targetProgress = Math.Max(targetProgress, 1);
                timer.Start();
                visual.Stage = "正在读取可更新软件清单…";
                worker.RunWorkerAsync();
            };
        }

        private static void LoadData(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker background = (BackgroundWorker)sender;
            var data = new StartupData();
            try
            {
                background.ReportProgress(0, "正在读取可更新软件清单…");
                data.UpdateScan = MainForm.RunWinget("upgrade --disable-interactivity");
                data.Updates = MainForm.ParseForSelfTest(data.UpdateScan.Output);
                background.ReportProgress(12, "可更新清单已读取，正在读取已安装软件…");
            }
            catch (Exception ex) { data.UpdateScan = new CommandResult { ExitCode = 1, Error = ex.Message, Output = "" }; }
            try
            {
                background.ReportProgress(14, "正在读取已安装软件清单…");
                data.InstalledScan = MainForm.RunWinget("list --disable-interactivity");
                data.InstalledPackages = UninstallForm.ParseForStartup(data.InstalledScan.Output);
                background.ReportProgress(22, "清单已读取，正在加载软件图标…");
            }
            catch (Exception ex) { data.InstalledScan = new CommandResult { ExitCode = 1, Error = ex.Message, Output = "" }; }

            var iconEntries = new List<Tuple<string, string>>();
            foreach (UpdateItem item in data.Updates) iconEntries.Add(Tuple.Create(item.Name, item.Id));
            foreach (InstalledPackage package in data.InstalledPackages) iconEntries.Add(Tuple.Create(package.Name, package.Id));
            var unique = new List<Tuple<string, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Tuple<string, string> entry in iconEntries)
                if (seen.Add(StartupData.IconKey(entry.Item2))) unique.Add(entry);
            background.ReportProgress(22, "正在快速准备图标预览…");
            for (int i = 0; i < unique.Count; i++)
            {
                Tuple<string, string> entry = unique[i];
                Image icon = null;
                try { icon = MainForm.ResolveAppIcon(entry.Item1, entry.Item2); }
                catch { }
                if (icon == null) icon = MainForm.MakeFallbackIcon(entry.Item1, entry.Item2);
                try { data.SetIcon(entry.Item2, icon); }
                finally { icon.Dispose(); }
                int percent = 22 + (unique.Count == 0 ? 0 : (int)(77.0 * (i + 1) / unique.Count));
                background.ReportProgress(percent, "正在加载软件图标… " + (i + 1) + " / " + unique.Count);
            }
            data.IconsLoaded = true;
            background.ReportProgress(99, "软件清单和图标已全部准备好");
            e.Result = data;
        }
    }

    internal sealed class LoadingVisual : Control
    {
        public int Progress;
        public int Frame;
        public string Stage = "正在准备软件…";

        public LoadingVisual()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float centerX = ClientSize.Width / 2F;
            float centerY = ClientSize.Height * 0.56F;
            float trackWidth = Math.Min(500F, ClientSize.Width * 0.68F);
            float trackLeft = centerX - trackWidth / 2F;
            float trackTop = centerY + 28F;
            using (var shadow = new SolidBrush(Color.FromArgb(70, 30, 35, 42)))
            using (var track = new SolidBrush(Color.FromArgb(105, 255, 255, 255)))
            using (var black = new SolidBrush(Color.FromArgb(25, 29, 34)))
            using (var titleBrush = new SolidBrush(Color.FromArgb(35, 42, 50)))
            using (var subBrush = new SolidBrush(Color.FromArgb(110, 110, 110)))
            using (var titleFont = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold))
            using (var stageFont = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                e.Graphics.DrawString("Lucky 软件更新器", titleFont, titleBrush, new RectangleF(0, centerY - 106F, ClientSize.Width, 34F), format);
                e.Graphics.DrawString(Stage ?? "正在准备软件…", stageFont, subBrush, new RectangleF(20F, centerY - 54F, ClientSize.Width - 40F, 24F), format);
                e.Graphics.FillRoundedRectangle(track, new RectangleF(trackLeft, trackTop, trackWidth, 11F), 6F);
                float fillWidth = trackWidth * Math.Max(0, Math.Min(100, Progress)) / 100F;
                if (fillWidth > 0F) e.Graphics.FillRoundedRectangle(black, new RectangleF(trackLeft, trackTop, fillWidth, 11F), 6F);
                float duckX = trackLeft + fillWidth - 25F;
                float duckBob = (float)Math.Sin(Frame * 0.16F) * 1.5F;
                DrawDuck(e.Graphics, duckX, trackTop - 22F + duckBob);
                e.Graphics.DrawString(Math.Max(0, Math.Min(100, Progress)).ToString(CultureInfo.InvariantCulture) + "%", stageFont, subBrush, new RectangleF(0F, trackTop + 18F, ClientSize.Width, 22F), format);
            }
        }

        private static void DrawDuck(Graphics graphics, float x, float y)
        {
            using (var body = new SolidBrush(Color.FromArgb(255, 219, 55)))
            using (var wing = new SolidBrush(Color.FromArgb(255, 232, 104)))
            using (var beak = new SolidBrush(Color.FromArgb(240, 139, 43)))
            using (var eye = new SolidBrush(Color.FromArgb(40, 39, 35)))
            using (var outline = new Pen(Color.FromArgb(205, 171, 50), 1.2F))
            {
                graphics.FillEllipse(body, x - 17F, y - 12F, 31F, 23F);
                graphics.DrawEllipse(outline, x - 17F, y - 12F, 31F, 23F);
                graphics.FillEllipse(body, x + 2F, y - 20F, 19F, 18F);
                graphics.DrawEllipse(outline, x + 2F, y - 20F, 19F, 18F);
                graphics.FillEllipse(wing, x - 9F, y - 7F, 13F, 10F);
                graphics.FillEllipse(eye, x + 14F, y - 14F, 2.7F, 2.7F);
                PointF[] beakPoints = { new PointF(x + 18F, y - 9F), new PointF(x + 27F, y - 6F), new PointF(x + 18F, y - 3F) };
                graphics.FillPolygon(beak, beakPoints);
            }
        }
    }

    internal static class GraphicsExtensions
    {
        internal static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rectangle, float radius)
        {
            if (rectangle.Width <= 0F || rectangle.Height <= 0F) return;
            float d = Math.Min(radius * 2F, Math.Min(rectangle.Width, rectangle.Height));
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(rectangle.Left, rectangle.Top, d, d, 180F, 90F);
                path.AddArc(rectangle.Right - d, rectangle.Top, d, d, 270F, 90F);
                path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0F, 90F);
                path.AddArc(rectangle.Left, rectangle.Bottom - d, d, d, 90F, 90F);
                path.CloseFigure();
                graphics.FillPath(brush, path);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private sealed class RegisteredIcon
        {
            public string DisplayName;
            public string NormalizedDisplayName;
            public string DisplayIcon;
        }

        private sealed class StartMenuIcon
        {
            public string Path;
            public string NormalizedName;
        }

        private static readonly object iconIndexLock = new object();
        private static List<StartMenuIcon> startMenuShortcutIndex;
        private static List<RegisteredIcon> registeredIconIndex;
        private readonly Color ink = Color.FromArgb(27, 39, 58);
        private readonly Color muted = Color.FromArgb(119, 133, 153);
        private readonly Color teal = Color.FromArgb(0, 157, 143);
        private readonly FlowLayoutPanel list;
        private readonly Label status;
        private readonly Label count;
        private readonly TextBox searchBox;
        private readonly Button refreshButton;
        private readonly Button logButton;
        private readonly Button historyButton;
        private readonly ProgressBar progress;
        private readonly BackgroundWorker worker;
        private readonly BackgroundWorker iconWorker;
        private readonly List<UpdateItem> items = new List<UpdateItem>();
        private readonly TableLayoutPanel contentSlots;
        private readonly FrostCardPanel detailPanel;
        private readonly Label detailName;
        private readonly Label detailId;
        private readonly Label detailVersions;
        private readonly Label detailSource;
        private readonly PillButton updateAction;
        private readonly StartupData startupData;
        private string lastLog = "";
        private UpdateItem pendingItem;
        private UpdateItem selectedItem;
        private bool busy;

        public MainForm() : this(null) { }

        internal MainForm(StartupData loadedData)
        {
            startupData = loadedData;
            Text = "Lucky 软件更新器";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(880, 600);
            Size = new Size(1060, 760);
            BackColor = Color.White;
            BackgroundImage = VisualTheme.LoadLandscape();
            BackgroundImageLayout = ImageLayout.Stretch;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            DoubleBuffered = true;
            if (File.Exists(Application.ExecutablePath))
            {
                try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { }
            }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);
            var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            root.Controls.Add(header, 0, 0);
            var iconBox = new PictureBox { Left = 32, Top = 18, Width = 48, Height = 48, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { iconBox.Image = Icon.ToBitmap(); }
            catch { }
            header.Controls.Add(iconBox);

            var title = new Label { Text = "软件更新器", Left = 94, Top = 12, AutoSize = true, ForeColor = ink, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold) };
            header.Controls.Add(title);
            var subtitle = new Label { Text = "选择一个图标，查看版本并单独更新", Left = 97, Top = 54, AutoSize = true, ForeColor = muted, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F) };
            header.Controls.Add(subtitle);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(32, 8, 32, 22), BackColor = Color.Transparent };
            root.Controls.Add(body, 0, 1);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 146F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            body.Controls.Add(layout);
            var searchPanel = new FrostCardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(12, 4, 12, 4) };
            layout.Controls.Add(searchPanel, 0, 0);
            var searchCaption = new Label { Text = "搜索", Dock = DockStyle.Left, Width = 54, TextAlign = ContentAlignment.MiddleLeft, ForeColor = muted, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            searchPanel.Controls.Add(searchCaption);
            searchBox = new TextBox { Left = 58, Top = 56, Width = 370, Height = 27, Anchor = AnchorStyles.Left | AnchorStyles.Top, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 9F) };
            searchPanel.Controls.Add(searchBox);
            searchBox.TextChanged += delegate { ApplySearchFilter(); };
            var clearSearch = new Button { Text = "清空", Left = 438, Top = 55, Width = 58, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(230, 236, 243), ForeColor = ink, Font = new Font("Microsoft YaHei UI", 8.5F), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            clearSearch.Click += delegate { searchBox.Clear(); searchBox.Focus(); };
            searchPanel.Controls.Add(clearSearch);
            var summary = new FrostCardPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 14, 0), Margin = new Padding(0, 0, 0, 8) };
            layout.Controls.Add(summary, 0, 1);
            status = new Label { Text = "正在准备更新清单…", Dock = DockStyle.Left, Width = 680, TextAlign = ContentAlignment.MiddleLeft, ForeColor = ink, BackColor = Color.Transparent };
            summary.Controls.Add(status);
            count = new Label { Text = "", Dock = DockStyle.Right, Width = 140, TextAlign = ContentAlignment.MiddleRight, ForeColor = teal, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            summary.Controls.Add(count);

            contentSlots = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            contentSlots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            contentSlots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0F));
            contentSlots.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.Controls.Add(contentSlots, 0, 2);
            list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Color.Transparent, Padding = new Padding(8, 8, 8, 8), Margin = new Padding(0) };
            contentSlots.Controls.Add(list, 0, 0);
            detailPanel = new FrostCardPanel { Left = 540, Top = 0, Width = 280, Height = 146, Padding = new Padding(10), Visible = false };
            searchPanel.Resize += delegate { detailPanel.Left = Math.Max(516, Math.Min(820 - detailPanel.Width, searchPanel.ClientSize.Width - detailPanel.Width - 12)); };
            searchPanel.Controls.Add(detailPanel);
            var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            detailPanel.Controls.Add(details);
            detailName = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = ink, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            detailId = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = muted, BackColor = Color.Transparent, Font = new Font("Consolas", 8.5F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            detailVersions = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = ink, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 10F), TextAlign = ContentAlignment.MiddleLeft };
            detailSource = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = muted, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 8.5F), TextAlign = ContentAlignment.TopLeft };
            details.Controls.Add(detailName, 0, 0);
            details.Controls.Add(detailId, 0, 1);
            details.Controls.Add(detailVersions, 0, 2);
            details.Controls.Add(detailSource, 0, 3);
            updateAction = new PillButton { Text = "更新", Dock = DockStyle.Fill, Margin = new Padding(1, 1, 1, 1), Enabled = false };
            updateAction.Click += delegate { if (selectedItem != null) UpdateItemNow(selectedItem); };
            details.Controls.Add(updateAction, 0, 4);

            var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            layout.Controls.Add(footer, 0, 3);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 492, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.FromArgb(244, 247, 251), Margin = new Padding(0), Padding = new Padding(0) };
            footer.Controls.Add(actions);
            progress = new ProgressBar { Dock = DockStyle.Right, Width = 170, Height = 12, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false, Margin = new Padding(0, 17, 0, 0) };
            footer.Controls.Add(progress);
            logButton = MakeButton("查看扫描日志", 132, Color.FromArgb(230, 236, 243), ink);
            logButton.Margin = new Padding(0, 4, 8, 0);
            logButton.Click += ShowLog;
            actions.Controls.Add(logButton);
            refreshButton = MakeButton("刷新清单", 112, Color.FromArgb(230, 236, 243), ink);
            refreshButton.Margin = new Padding(0, 4, 8, 0);
            refreshButton.Click += delegate { Scan(); };
            actions.Controls.Add(refreshButton);
            historyButton = MakeButton("更新记录", 104, Color.FromArgb(230, 236, 243), ink);
            historyButton.Margin = new Padding(0, 4, 0, 0);
            historyButton.Click += ShowHistory;
            actions.Controls.Add(historyButton);
            Button uninstallButton = MakeButton("卸载软件", 96, Color.FromArgb(255, 239, 222), Color.FromArgb(153, 81, 20));
            uninstallButton.Margin = new Padding(8, 4, 0, 0);
            uninstallButton.Click += delegate { using (UninstallForm dialog = new UninstallForm(startupData)) dialog.ShowDialog(this); };
            actions.Controls.Add(uninstallButton);

            worker = new BackgroundWorker();
            worker.DoWork += WorkerDoWork;
            worker.RunWorkerCompleted += WorkerCompleted;
            iconWorker = new BackgroundWorker { WorkerReportsProgress = true, WorkerSupportsCancellation = true };
            iconWorker.DoWork += LoadRealIcons;
            iconWorker.ProgressChanged += ApplyLoadedIcon;
            FormClosed += delegate { if (iconWorker.IsBusy) iconWorker.CancelAsync(); };
            Load += delegate
            {
                if (startupData == null) Scan();
                else LoadStartupData();
            };
            if (startupData == null)
            {
                var startupVisual = new LoadingVisual { Dock = DockStyle.Fill, Stage = "正在打开更新器，软件清单将在后台扫描…" };
                Controls.Add(startupVisual);
                startupVisual.BringToFront();
                Shown += delegate
                {
                    var animationClock = Stopwatch.StartNew();
                    var animationTimer = new Timer { Interval = 30 };
                    animationTimer.Tick += delegate
                    {
                        startupVisual.Frame++;
                        startupVisual.Progress = Math.Min(100, (int)(animationClock.ElapsedMilliseconds * 100 / 1800));
                        startupVisual.Invalidate();
                        if (startupVisual.Progress >= 100)
                        {
                            animationTimer.Stop();
                            animationTimer.Dispose();
                            Controls.Remove(startupVisual);
                            startupVisual.Dispose();
                        }
                    };
                    animationTimer.Start();
                };
            }
        }

        private void LoadStartupData()
        {
            if (startupData == null) { Scan(); return; }
            lastLog = startupData.UpdateScan == null ? "" : startupData.UpdateScan.Output + (String.IsNullOrEmpty(startupData.UpdateScan.Error) ? "" : Environment.NewLine + startupData.UpdateScan.Error);
            foreach (UpdateItem item in startupData.Updates) AddRow(item);
            if (startupData.UpdateScan != null && startupData.UpdateScan.ExitCode != 0)
            {
                status.Text = "更新清单读取未完全成功；可查看日志或刷新。";
                count.Text = startupData.Updates.Count == 0 ? "" : startupData.Updates.Count + " 项已识别";
            }
            else
            {
                status.Text = startupData.Updates.Count == 0 ? "没有识别到可更新项目；可查看日志或刷新。" : "扫描完成。点击某一行的“更新”只处理该软件。";
                count.Text = startupData.Updates.Count + " 项可更新";
            }
            ApplySearchFilter();
            if (!startupData.IconsLoaded && !iconWorker.IsBusy) iconWorker.RunWorkerAsync(startupData);
        }

        private void LoadRealIcons(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker background = (BackgroundWorker)sender;
            StartupData data = (StartupData)e.Argument;
            var entries = new List<Tuple<string, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UpdateItem item in data.Updates)
                if (seen.Add(item.Id)) entries.Add(Tuple.Create(item.Id, item.Name));
            foreach (InstalledPackage package in data.InstalledPackages)
                if (seen.Add(package.Id)) entries.Add(Tuple.Create(package.Id, package.Name));
            foreach (Tuple<string, string> entry in entries)
            {
                if (background.CancellationPending) { e.Cancel = true; return; }
                Image loaded = null;
                try { loaded = ResolveAppIcon(entry.Item2, entry.Item1); } catch { }
                if (background.CancellationPending) { if (loaded != null) loaded.Dispose(); e.Cancel = true; return; }
                if (loaded != null) background.ReportProgress(0, Tuple.Create(entry.Item1, loaded));
            }
        }

        private void ApplyLoadedIcon(object sender, ProgressChangedEventArgs e)
        {
            if (startupData == null || IsDisposed || Disposing) return;
            Tuple<string, Image> loaded = e.UserState as Tuple<string, Image>;
            if (loaded == null || loaded.Item2 == null) return;
            using (loaded.Item2) startupData.SetIcon(loaded.Item1, loaded.Item2);
            foreach (UpdateItem item in items)
                if (String.Equals(item.Id, loaded.Item1, StringComparison.OrdinalIgnoreCase) && item.IconBox != null && !item.IconBox.IsDisposed)
                    ReplacePictureIcon(item.IconBox, startupData.IconCache[StartupData.IconKey(item.Id)]);
        }

        internal static void ReplacePictureIcon(PictureBox picture, Image replacement)
        {
            if (picture == null || picture.IsDisposed || replacement == null) return;
            Image old = picture.Image;
            picture.Image = new Bitmap(replacement);
            if (old != null) old.Dispose();
        }

        private static Button MakeButton(string text, int width, Color back, Color fore)
        {
            return new Button { Text = text, Width = width, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        }

        private Label Column(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = muted, Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold) };
        }

        private static void AddColumnWidths(TableLayoutPanel table)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13F));
        }

        private void Scan()
        {
            if (busy) return;
            busy = true;
            refreshButton.Enabled = false;
            progress.Visible = true;
            status.Text = "正在查询已配置的软件源…只扫描，不会安装或更新软件。";
            count.Text = "";
            ClearSelection();
            foreach (UpdateItem oldItem in items)
                if (oldItem.IconBox != null && oldItem.IconBox.Image != null) oldItem.IconBox.Image.Dispose();
            list.Controls.Clear();
            items.Clear();
            worker.RunWorkerAsync("scan");
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            bool scanning = e.Argument is string && (string)e.Argument == "scan";
            string mode = scanning ? "scan" : "update";
            if (scanning)
            {
                CommandResult result = RunWinget("upgrade --disable-interactivity");
                e.Result = new object[] { mode, result, ParseUpdates(result.Output) };
            }
            else
            {
                UpdateItem item = ((UpdateJob)e.Argument).Item;
                string source = item.Source;
                string args = "upgrade --id \"" + item.Id + "\" --exact --accept-source-agreements --accept-package-agreements --disable-interactivity";
                if (!String.IsNullOrEmpty(source) && Regex.IsMatch(source, "^[A-Za-z0-9._-]+$")) args += " --source \"" + source + "\"";
                CommandResult result = RunWinget(args);
                e.Result = new object[] { mode, result, item };
            }
        }

        internal static string PerformPreUninstallAction(CleanupCandidate candidate)
        {
            if (candidate.ActionKind == "KillProcess")
            {
                try
                {
                    using (Process process = Process.GetProcessById(candidate.ProcessId))
                    {
                        string executable = process.MainModule == null ? "" : Path.GetFullPath(process.MainModule.FileName);
                        if (!executable.Equals(Path.GetFullPath(candidate.Path), StringComparison.OrdinalIgnoreCase) || ResidueScanner.IsProtected(executable)) return "进程归属已变化，拒绝结束";
                        process.Kill();
                        if (!process.WaitForExit(5000)) return "已请求结束进程，但仍在运行";
                        return "已结束进程 PID " + candidate.ProcessId;
                    }
                }
                catch (ArgumentException) { return "进程已自行退出"; }
                catch (Exception ex) { return "结束进程失败：" + ex.Message; }
            }
            if (candidate.ActionKind == "StopService")
            {
                if (!IsSafeServiceName(candidate.ServiceName) || !ServiceEvidenceMatches(candidate)) return "服务 ImagePath 已变化，拒绝停止";
                CommandResult result = RunSc("stop", candidate.ServiceName);
                return result.ExitCode == 0 ? "已请求停止服务 " + candidate.ServiceName + "：" + result.Output.Trim() : "停止服务失败：" + result.Output.Trim();
            }
            return "无需卸载前处理";
        }

        internal static CommandResult RunSelectedUninstaller(UninstallJob job)
        {
            if (job.Uninstaller.UseWinget)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
                string exe = File.Exists(alias) ? alias : "winget.exe";
                string args = "uninstall --id \"" + job.Package.Id + "\" --exact --accept-source-agreements";
                if (!String.IsNullOrEmpty(job.Package.Source) && Regex.IsMatch(job.Package.Source, "^[A-Za-z0-9._-]+$")) args += " --source \"" + job.Package.Source + "\"";
                var info = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = false };
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit();
                    return new CommandResult { ExitCode = process.ExitCode, Output = "已使用可见 Winget 默认模式（未传 --silent）。" };
                }
            }
            var uninstaller = new ProcessStartInfo(job.Uninstaller.Executable, job.Uninstaller.Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = job.Uninstaller.IsQuiet
            };
            using (Process process = Process.Start(uninstaller))
            {
                process.WaitForExit();
                return new CommandResult { ExitCode = process.ExitCode, Output = job.Uninstaller.IsQuiet ? "已运行登记的静默卸载命令。" : "已运行普通卸载命令；卸载器界面保持可见。" };
            }
        }

        internal static string PerformPostUninstallAction(CleanupCandidate candidate, out long freed)
        {
            freed = 0;
            try
            {
                if (candidate.ActionKind == "DeleteService")
                {
                    if (!IsSafeServiceName(candidate.ServiceName)) return "服务名不安全，拒绝删除";
                    if (!ServiceKeyExists(candidate.ServiceName)) return "服务已由官方卸载器删除";
                    if (!ServiceEvidenceMatches(candidate)) return "服务 ImagePath 已变化，拒绝删除";
                    CommandResult result = RunSc("delete", candidate.ServiceName);
                    return result.ExitCode == 0 ? "已请求删除服务 " + candidate.ServiceName + "；系统可能延迟清除此服务键" : "删除服务失败：" + result.Output.Trim();
                }
                if (candidate.ActionKind == "DeleteRegistryValue")
                {
                    if (!IsAllowedRunKey(candidate.RegistryKeyPath)) return "注册表路径不在允许的自启动位置，拒绝删除";
                    using (RegistryKey key = OpenRegistry(candidate.RegistryHive, candidate.RegistryKeyPath, true))
                    {
                        if (key == null) return "自启动项已不存在";
                        string current = Convert.ToString(key.GetValue(candidate.RegistryValueName));
                        if (!String.Equals(current, candidate.RegistryValueData, StringComparison.Ordinal)) return "自启动项内容已变化，拒绝删除";
                        key.DeleteValue(candidate.RegistryValueName, false);
                        return "已删除自启动项";
                    }
                }
                if (candidate.ActionKind == "DeleteRegistryKey")
                {
                    if (!IsSafeProductRegistryKey(candidate.RegistryKeyPath)) return "注册表键不在允许的产品位置，拒绝删除";
                    int split = candidate.RegistryKeyPath.LastIndexOf('\\');
                    if (split <= 0) return "注册表键路径无效";
                    string parentPath = candidate.RegistryKeyPath.Substring(0, split);
                    string leaf = candidate.RegistryKeyPath.Substring(split + 1);
                    using (RegistryKey parent = OpenRegistry(candidate.RegistryHive, parentPath, true))
                    {
                        if (parent == null || parent.OpenSubKey(leaf) == null) return "产品注册表键已不存在";
                        parent.DeleteSubKeyTree(leaf, false);
                        return "已删除产品注册表键";
                    }
                }
                if (candidate.ActionKind == "EmptyDirectory")
                {
                    if (ResidueScanner.IsProtected(candidate.Path)) return "保护路径拦截";
                    int removed = RemoveEmptyDirectories(candidate.Path);
                    return removed > 0 ? "已移除 " + removed + " 个空目录" : "目录非空，已保留";
                }
                if (candidate.ActionKind == "DeleteFile")
                {
                    if (ResidueScanner.IsProtected(candidate.Path)) return "保护路径拦截";
                    if (!File.Exists(candidate.Path)) return "已不存在";
                    long fileSize = new FileInfo(candidate.Path).Length;
                    File.Delete(candidate.Path);
                    if (File.Exists(candidate.Path)) return "清理后仍存在";
                    freed = fileSize;
                    return "已清理快捷方式";
                }
                if (candidate.ActionKind == "DeleteDirectory")
                {
                    if (ResidueScanner.IsProtected(candidate.Path)) return "保护路径拦截";
                    if (!Directory.Exists(candidate.Path)) return "已不存在";
                    if ((File.GetAttributes(candidate.Path) & FileAttributes.ReparsePoint) != 0 || ResidueScanner.ContainsReparsePoint(candidate.Path)) return "包含链接/重解析点，为保护目标而跳过";
                    Directory.Delete(candidate.Path, true);
                    if (Directory.Exists(candidate.Path)) return "清理后仍存在";
                    freed = candidate.Size;
                    return "已清理数据目录";
                }
            }
            catch (Exception ex) { return "操作失败：" + ex.Message; }
            return "未识别的操作，已跳过";
        }

        private static int RemoveEmptyDirectories(string root)
        {
            if (!Directory.Exists(root)) return 0;
            if (ResidueScanner.IsProtected(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return 0;
            int removed = 0;
            foreach (string child in Directory.GetDirectories(root)) removed += RemoveEmptyDirectories(child);
            if (!Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root, false);
                removed++;
            }
            return removed;
        }

        internal static string VerifyCandidate(CleanupCandidate candidate)
        {
            try
            {
                if (candidate.ActionKind == "KillProcess")
                {
                    using (Process process = Process.GetProcessById(candidate.ProcessId))
                    {
                        string path = process.MainModule == null ? "" : Path.GetFullPath(process.MainModule.FileName);
                        return path.Equals(Path.GetFullPath(candidate.Path), StringComparison.OrdinalIgnoreCase) ? "进程仍在运行" : "原进程已结束（PID 已复用）";
                    }
                }
                if (candidate.ActionKind == "StopService")
                {
                    if (!ServiceKeyExists(candidate.ServiceName)) return "服务已被卸载器移除";
                    CommandResult query = RunSc("query", candidate.ServiceName);
                    return query.Output.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0 ? "服务已停止" : "服务仍运行或状态不可读";
                }
                if (candidate.ActionKind == "DeleteService") return ServiceKeyExists(candidate.ServiceName) ? "服务项仍存在/等待重启清除" : "服务项已消失";
                if (candidate.ActionKind == "DeleteRegistryValue")
                {
                    using (RegistryKey key = OpenRegistry(candidate.RegistryHive, candidate.RegistryKeyPath, false))
                        return key == null || !key.GetValueNames().Contains(candidate.RegistryValueName, StringComparer.OrdinalIgnoreCase) ? "自启动值已消失" : "自启动值仍存在";
                }
                if (candidate.ActionKind == "DeleteRegistryKey")
                {
                    using (RegistryKey key = OpenRegistry(candidate.RegistryHive, candidate.RegistryKeyPath, false)) return key == null ? "注册表键已消失" : "注册表键仍存在";
                }
                bool exists = candidate.IsFile ? File.Exists(candidate.Path) : Directory.Exists(candidate.Path);
                return exists ? "仍存在（未清理或清理失败）" : "已消失";
            }
            catch (ArgumentException) { return "进程已结束"; }
            catch (Exception ex) { return "复查失败：" + ex.Message; }
        }

        private static bool ServiceEvidenceMatches(CleanupCandidate candidate)
        {
            using (RegistryKey key = OpenRegistry("HKLM", candidate.RegistryKeyPath, false))
            {
                if (key == null) return false;
                string image = Convert.ToString(key.GetValue("ImagePath"));
                return String.Equals(image, candidate.RegistryValueData, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool ServiceKeyExists(string serviceName)
        {
            if (!IsSafeServiceName(serviceName)) return false;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName)) return key != null;
        }

        private static bool IsSafeServiceName(string value) { return !String.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[A-Za-z0-9_.-]+$"); }
        private static bool IsAllowedRunKey(string path) { return path == @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" || path == @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"; }

        private static bool IsSafeProductRegistryKey(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !path.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (path.IndexOf("..", StringComparison.Ordinal) >= 0 || path.IndexOf("CurrentVersion\\Uninstall", StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith("\\Run", StringComparison.OrdinalIgnoreCase)) return false;
            string leaf = path.Substring(path.LastIndexOf('\\') + 1);
            string norm = Regex.Replace(leaf.ToLowerInvariant(), "[^\\p{L}\\p{N}]", "");
            return !String.IsNullOrEmpty(norm) && norm != "microsoft" && norm != "windows" && norm != "classes" && norm != "policies";
        }

        private static RegistryKey OpenRegistry(string hive, string path, bool writable)
        {
            RegistryKey root = hive == "HKLM" ? Registry.LocalMachine : hive == "HKCU" ? Registry.CurrentUser : null;
            return root == null ? null : root.OpenSubKey(path, writable);
        }

        private static CommandResult RunSc(string operation, string serviceName)
        {
            if (!IsSafeServiceName(serviceName)) return new CommandResult { ExitCode = 1, Output = "服务名格式无效" };
            string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
            var info = new ProcessStartInfo(sc, operation + " \"" + serviceName + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return new CommandResult { ExitCode = process.ExitCode, Output = output + error };
            }
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            busy = false;
            refreshButton.Enabled = true;
            progress.Visible = false;
            updateAction.Enabled = selectedItem != null;
            if (e.Error != null)
            {
                lastLog = e.Error.ToString();
                status.Text = "操作失败：" + e.Error.Message;
                if (pendingItem != null)
                {
                    AppendHistory(pendingItem, "启动失败", "");
                    pendingItem = null;
                }
                return;
            }
            object[] data = (object[])e.Result;
            string mode = (string)data[0];
            CommandResult result = (CommandResult)data[1];
            lastLog = result.Output + (String.IsNullOrEmpty(result.Error) ? "" : Environment.NewLine + result.Error);
            if (mode == "scan")
            {
                List<UpdateItem> found = (List<UpdateItem>)data[2];
                foreach (UpdateItem item in found) AddRow(item);
                if (result.ExitCode != 0)
                {
                    string diagnostic = (result.Output + " " + result.Error).ToLowerInvariant();
                    status.Text = diagnostic.Contains("source agreement") || diagnostic.Contains("源协议")
                        ? "Winget 需要先确认软件源协议；查看日志，确认来源后再重试。"
                        : "扫描未成功（代码 " + result.ExitCode + "）。打开扫描日志查看原因。";
                    count.Text = found.Count == 0 ? "" : found.Count + " 项已识别";
                }
                else if (found.Count == 0)
                {
                    status.Text = "没有识别到可更新项目，或 Winget 输出格式暂不兼容。可查看扫描日志。";
                    count.Text = "0 项";
                }
                else
                {
                    status.Text = "扫描完成。点击某一行的“更新”只处理该软件。";
                    count.Text = found.Count + " 项可更新";
                }
                ApplySearchFilter();
            }
            else
            {
                UpdateItem item = (UpdateItem)data[2];
                AppendHistory(item, result.ExitCode == 0 ? "Winget 返回成功" : "失败", result.ExitCode.ToString(CultureInfo.InvariantCulture));
                pendingItem = null;
                if (result.ExitCode == 0)
                {
                    status.Text = item.Name + "：Winget 已完成命令，正在刷新清单…";
                    Scan();
                }
                else status.Text = item.Name + " 更新未完成（代码 " + result.ExitCode + "）。查看扫描日志。";
            }
        }

        private void UpdateItemNow(UpdateItem item)
        {
            bool isWorkBuddy = IsWorkBuddy(item);
            if (isWorkBuddy)
            {
                string warning = "更新 WorkBuddy 可能会关闭或重启它。如果你正在通过 WorkBuddy 使用当前对话，对话可能中断。\r\n\r\n建议先结束对话并退出 WorkBuddy，再继续更新。仍要继续吗？";
                if (MessageBox.Show(this, warning, "更新 WorkBuddy 前请留意", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            if (busy) return;
            busy = true;
            pendingItem = item;
            refreshButton.Enabled = false;
            foreach (UpdateItem row in items) row.UpdateButton.Enabled = false;
            progress.Visible = true;
            status.Text = "正在更新 " + item.Name + "…";
            worker.RunWorkerAsync(new UpdateJob { Item = item });
        }

        internal static bool IsWorkBuddy(UpdateItem item)
        {
            return item != null && (NormalizeName(item.Name).Contains("workbuddy") || NormalizeName(item.Id).Contains("workbuddy"));
        }

        private sealed class UpdateJob
        {
            public UpdateItem Item;
        }

        private void AddRow(UpdateItem item)
        {
            items.Add(item);
            Panel row = new Panel { Width = 100, Height = 112, BackColor = Color.Transparent, Margin = new Padding(4, 4, 4, 4), Padding = new Padding(0), Cursor = Cursors.Hand };
            item.Row = row;
            PictureBox appIcon = new PictureBox { Left = 22, Top = 3, Width = 56, Height = 56, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Cursor = Cursors.Hand };
            appIcon.Image = ResolveCachedIcon(item.Name, item.Id, startupData == null ? null : startupData.IconCache);
            item.IconBox = appIcon;
            var name = new Label { Text = item.Name, Left = 0, Top = 64, Width = 100, Height = 42, ForeColor = ink, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular), AutoEllipsis = true, TextAlign = ContentAlignment.TopCenter, Cursor = Cursors.Hand };
            row.Controls.Add(appIcon);
            row.Controls.Add(name);
            row.Click += delegate { SelectItem(item); };
            appIcon.Click += delegate { SelectItem(item); };
            name.Click += delegate { SelectItem(item); };
            item.UpdateButton = updateAction;
            list.Controls.Add(row);
            ApplySearchFilter();
        }

        private void SelectItem(UpdateItem item)
        {
            selectedItem = item;
            detailName.Text = item.Name;
            detailId.Text = item.Id;
            detailVersions.Text = item.CurrentVersion + "   →   " + item.AvailableVersion;
            detailSource.Text = "来源  " + (String.IsNullOrEmpty(item.Source) ? "未标明" : item.Source);
            detailPanel.Visible = true;
            updateAction.Enabled = !busy;
        }

        private void ClearSelection()
        {
            selectedItem = null;
            if (detailPanel != null) detailPanel.Visible = false;
            if (contentSlots != null && contentSlots.ColumnStyles.Count > 1) contentSlots.ColumnStyles[1].Width = 0F;
            if (updateAction != null) updateAction.Enabled = false;
        }

        private void ApplySearchFilter()
        {
            string query = searchBox == null ? "" : searchBox.Text.Trim();
            int visible = 0;
            foreach (UpdateItem item in items)
            {
                bool match = MatchesSearch(item, query);
                if (item.Row != null) item.Row.Visible = match;
                if (match) visible++;
            }
            if (selectedItem != null && !MatchesSearch(selectedItem, query)) ClearSelection();
            if (!String.IsNullOrEmpty(query)) count.Text = visible + " / " + items.Count + " 项";
            else if (items.Count > 0) count.Text = items.Count + " 项可更新";
        }

        private static bool MatchesSearch(UpdateItem item, string query)
        {
            if (String.IsNullOrEmpty(query)) return true;
            string searchable = String.Join(" ", new string[] { item.Name, item.Id, item.Source, item.CurrentVersion, item.AvailableVersion });
            return searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ResizeRows() { }

        internal static bool ValidateRowLayout()
        {
            using (MainForm form = new MainForm())
            {
                form.Size = new Size(1060, 760);
                string[] names = { "微信", "Microsoft Outlook", "Epic Games Launcher", "5EClient", "NVIDIA App", "VC++ Runtime", "Wallpaper Engine" };
                for (int i = 0; i < names.Length; i++)
                    form.AddRow(new UpdateItem { Name = names[i], Id = "Example.Package" + i, CurrentVersion = "1.0.0", AvailableVersion = "1.1.0", Source = i == 1 ? "msstore" : "winget" });
                form.CreateControl();
                form.PerformLayout();
                form.list.CreateControl();
                form.list.PerformLayout();
                int previousLeft = -1;
                foreach (UpdateItem item in form.items)
                {
                    item.Row.CreateControl();
                    item.Row.PerformLayout();
                    if (item.Row.Width != 100 || item.Row.Height != 112 || item.IconBox == null || item.IconBox.Image == null) return false;
                    previousLeft = item.Row.Left;
                }
                return form.list.Height > 100 && !form.list.AutoScroll && form.list.WrapContents && form.items.Count == names.Length && form.detailPanel.Visible == false;
            }
        }

        internal static bool ValidateSearchFilter()
        {
            using (MainForm form = new MainForm())
            {
                form.Size = new Size(1060, 760);
                string[] names = { "微信", "NVIDIA App", "Epic Games Launcher" };
                for (int i = 0; i < names.Length; i++)
                    form.AddRow(new UpdateItem { Name = names[i], Id = "Example.Package" + i, CurrentVersion = "1.0.0", AvailableVersion = "1.1.0", Source = "winget" });
                form.searchBox.Text = "nvidia";
                bool narrowed = !MatchesSearch(form.items[0], form.searchBox.Text) && MatchesSearch(form.items[1], form.searchBox.Text) && !MatchesSearch(form.items[2], form.searchBox.Text);
                form.searchBox.Clear();
                return narrowed && form.items.TrueForAll(delegate(UpdateItem item) { return MatchesSearch(item, form.searchBox.Text); });
            }
        }

        internal static CommandResult RunWinget(string arguments)
        {
            string exe = FindWinget();
            var info = new ProcessStartInfo(exe, arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (Process process = Process.Start(info))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                Task.WaitAll(stdout, stderr);
                return new CommandResult { ExitCode = process.ExitCode, Output = StripAnsi(stdout.Result), Error = StripAnsi(stderr.Result) };
            }
        }

        private static string FindWinget()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(alias)) return alias;
            return "winget.exe";
        }

        private static string StripAnsi(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            value = Regex.Replace(value, "\\x1B\\[[0-?]*[ -/]*[@-~]", "");
            return value.Replace("\r", "\n");
        }

        private static List<UpdateItem> ParseUpdates(string output)
        {
            var result = new List<UpdateItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = (output ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("-")) continue;
                string[] parts = Regex.Split(line, "\\s{2,}");
                if (parts.Length < 4) continue;
                string id = parts[1].Trim();
                if (!PackageIdPolicy.IsWingetId(id)) continue;
                if (parts[0].Equals("Name", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("名称", StringComparison.Ordinal)) continue;
                if (seen.Contains(id)) continue;
                string source = parts.Length > 4 ? parts[4].Trim() : "";
                result.Add(new UpdateItem { Name = parts[0].Trim(), Id = id, CurrentVersion = parts[2].Trim(), AvailableVersion = parts[3].Trim(), Source = source });
                seen.Add(id);
            }
            return result;
        }

        internal static List<UpdateItem> ParseForSelfTest(string output)
        {
            return ParseUpdates(output);
        }

        private void ShowLog(object sender, EventArgs e)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "Winget 扫描日志";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(850, 520);
                var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F), Text = String.IsNullOrEmpty(lastLog) ? "暂无日志。" : lastLog };
                dialog.Controls.Add(text);
                dialog.ShowDialog(this);
            }
        }

        private static string HistoryPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuckyUpdater", "update-history.tsv");
        }

        private static string CleanField(string value)
        {
            return (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }

        private static void AppendHistory(UpdateItem item, string result, string exitCode)
        {
            try
            {
                string path = HistoryPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                bool exists = File.Exists(path);
                using (StreamWriter writer = new StreamWriter(path, true, Encoding.UTF8))
                {
                    if (!exists) writer.WriteLine("时间\t软件\t当前版本\t目标版本\t包 ID\t来源\t结果\t退出代码");
                    writer.WriteLine(String.Join("\t", new string[] { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), CleanField(item.Name), CleanField(item.CurrentVersion), CleanField(item.AvailableVersion), CleanField(item.Id), CleanField(item.Source), CleanField(result), CleanField(exitCode) }));
                }
            }
            catch { }
        }

        private void ShowHistory(object sender, EventArgs e)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "更新记录";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(960, 500);
                string path = HistoryPath();
                if (!File.Exists(path))
                {
                    dialog.Controls.Add(new Label { Text = "还没有更新记录。每次点击更新后，程序会记录软件、版本、来源、时间和结果。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = muted });
                }
                else
                {
                    var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, RowHeadersVisible = false };
                    string[] headers = { "时间", "软件", "当前版本", "目标版本", "包 ID", "来源", "结果", "退出代码" };
                    foreach (string header in headers) grid.Columns.Add(header, header);
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    for (int i = lines.Length - 1; i >= 1; i--)
                    {
                        string[] fields = lines[i].Split('\t');
                        if (fields.Length == headers.Length) grid.Rows.Add(fields);
                    }
                    dialog.Controls.Add(grid);
                }
                dialog.ShowDialog(this);
            }
        }

        internal static Image ResolveAppIcon(string name, string id)
        {
            Image shortcutIcon = FindStartMenuIcon(name, id);
            if (shortcutIcon != null) return shortcutIcon;
            string wanted = NormalizeName(name);
            string bestPath = null;
            int bestScore = 0;
            foreach (RegisteredIcon entry in GetRegisteredIconIndex())
            {
                int score = NameScore(wanted, entry.NormalizedDisplayName);
                if (score > bestScore && !String.IsNullOrEmpty(entry.DisplayIcon)) { bestScore = score; bestPath = entry.DisplayIcon; }
            }
            if (bestScore >= 75)
            {
                string path = bestPath.Trim().Trim('"');
                int iconIndex = 0;
                int comma = path.LastIndexOf(',');
                if (comma > 0 && Regex.IsMatch(path.Substring(comma + 1).Trim(), "^-?[0-9]+$"))
                {
                    Int32.TryParse(path.Substring(comma + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out iconIndex);
                    path = path.Substring(0, comma).Trim().Trim('"');
                }
                try
                {
                    path = Environment.ExpandEnvironmentVariables(path);
                    Image extracted = ExtractRegisteredIcon(path, iconIndex);
                    if (extracted != null) return extracted;
                }
                catch { }
            }
            return MakeFallbackIcon(name, id);
        }

        internal static Image ResolveCachedIcon(string name, string id, Dictionary<string, Image> cache)
        {
            Image cached;
            if (cache != null && cache.TryGetValue(StartupData.IconKey(id), out cached) && cached != null) return new Bitmap(cached);
            return ResolveAppIcon(name, id);
        }

        private static Image FindStartMenuIcon(string name, string id)
        {
            string bestShortcut = null;
            int bestScore = 0;
            string app = NormalizeName(name);
            string package = NormalizeName(id);
            foreach (StartMenuIcon shortcut in GetStartMenuShortcutIndex())
            {
                int score = ShortcutNameScore(app, package, shortcut.NormalizedName);
                if (score > bestScore) { bestScore = score; bestShortcut = shortcut.Path; }
            }
            if (bestShortcut == null || bestScore < 70) return null;
            object shell = null;
            object link = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { bestShortcut });
                Type linkType = link.GetType();
                string target = Convert.ToString(linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null));
                string iconLocation = Convert.ToString(linkType.InvokeMember("IconLocation", System.Reflection.BindingFlags.GetProperty, null, link, null));
                if (IsProtectedPath(target) || IsProtectedPath(iconLocation)) return null;
                if (!String.IsNullOrEmpty(target) && Path.IsPathRooted(target) && !File.Exists(target)) return null;
                using (Icon icon = Icon.ExtractAssociatedIcon(bestShortcut))
                    if (icon != null) return icon.ToBitmap();
            }
            catch { }
            finally
            {
                if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
            return null;
        }

        private static List<StartMenuIcon> GetStartMenuShortcutIndex()
        {
            lock (iconIndexLock)
            {
                if (startMenuShortcutIndex != null) return startMenuShortcutIndex;
                var shortcuts = new List<StartMenuIcon>();
                string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) };
                foreach (string root in roots)
                {
                    if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                    var pending = new Stack<string>();
                    pending.Push(root);
                    while (pending.Count > 0)
                    {
                        string directory = pending.Pop();
                        try
                        {
                            foreach (string shortcut in Directory.GetFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
                                shortcuts.Add(new StartMenuIcon { Path = shortcut, NormalizedName = NormalizeName(Path.GetFileNameWithoutExtension(shortcut)) });
                        }
                        catch { }
                        try { foreach (string child in Directory.GetDirectories(directory)) pending.Push(child); } catch { }
                    }
                }
                startMenuShortcutIndex = shortcuts;
                return startMenuShortcutIndex;
            }
        }

        private static List<RegisteredIcon> GetRegisteredIconIndex()
        {
            lock (iconIndexLock)
            {
                if (registeredIconIndex != null) return registeredIconIndex;
                var entries = new List<RegisteredIcon>();
                RegistryKey[] roots = { Registry.LocalMachine, Registry.CurrentUser };
                string[] subpaths = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
                foreach (RegistryKey root in roots)
                    foreach (string subpath in subpaths)
                    {
                        try
                        {
                            using (RegistryKey uninstall = root.OpenSubKey(subpath))
                            {
                                if (uninstall == null) continue;
                                foreach (string keyName in uninstall.GetSubKeyNames())
                                {
                                    try
                                    {
                                        using (RegistryKey app = uninstall.OpenSubKey(keyName))
                                        {
                                            if (app == null) continue;
                                            string displayName = Convert.ToString(app.GetValue("DisplayName"));
                                            string displayIcon = Convert.ToString(app.GetValue("DisplayIcon"));
                                            if (!String.IsNullOrWhiteSpace(displayName) && !String.IsNullOrWhiteSpace(displayIcon)) entries.Add(new RegisteredIcon { DisplayName = displayName, NormalizedDisplayName = NormalizeName(displayName), DisplayIcon = displayIcon });
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                registeredIconIndex = entries;
                return registeredIconIndex;
            }
        }

        private static int ShortcutNameScore(string app, string package, string candidate)
        {
            int score = NameScore(app, candidate);
            if ((app.Contains("5e") || package.Contains("5eclient")) && candidate.Contains("5e")) return Math.Max(score, 85);
            if ((app.Contains("wechat") || package.Contains("wechat") || app.Contains("微信")) && candidate.Contains("微信")) return Math.Max(score, 85);
            if ((app.Contains("qq") || package.Contains("qq")) && (candidate.Contains("qq") || candidate.Contains("腾讯"))) return Math.Max(score, 85);
            return score;
        }

        private static bool IsProtectedPath(string path)
        {
            return ProtectedPathPolicy.IsProtected(path);
        }

        private static string NormalizeName(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var normalized = new StringBuilder(value.Length);
            foreach (char character in value)
                if (Char.IsLetterOrDigit(character)) normalized.Append(Char.ToLowerInvariant(character));
            return normalized.ToString();
        }

        private static int NameScore(string wanted, string candidate)
        {
            if (wanted.Length == 0 || candidate.Length == 0) return 0;
            if (wanted == candidate) return 100;
            if (wanted.Length >= 4 && candidate.Length >= 4 && (wanted.StartsWith(candidate) || candidate.StartsWith(wanted) || wanted.Contains(candidate) || candidate.Contains(wanted))) return 80;
            return 0;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW", SetLastError = true)]
        private static extern uint ExtractIconEx(string file, int index, IntPtr[] largeIcons, IntPtr[] smallIcons, uint iconCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Image ExtractRegisteredIcon(string path, int index)
        {
            if (IsProtectedPath(path) || !File.Exists(path)) return null;
            string extension = Path.GetExtension(path);
            if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using (Icon icon = new Icon(path)) return icon.ToBitmap();
            }
            IntPtr[] large = new IntPtr[1];
            IntPtr[] small = new IntPtr[1];
            try
            {
                if (ExtractIconEx(path, index, large, small, 1) == 0) return null;
                IntPtr handle = large[0] != IntPtr.Zero ? large[0] : small[0];
                if (handle == IntPtr.Zero) return null;
                using (Icon icon = Icon.FromHandle(handle)) return icon.ToBitmap();
            }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero && small[0] != large[0]) DestroyIcon(small[0]);
            }
        }

        internal static Image MakeFallbackIcon(string name, string id)
        {
            Bitmap bitmap = new Bitmap(42, 42);
            int hash = (id ?? name ?? "").GetHashCode() & 0x7fffffff;
            Color back = Color.FromArgb(255, 70 + hash % 90, 90 + (hash / 7) % 90, 120 + (hash / 31) % 80);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Font font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(back))
            using (SolidBrush white = new SolidBrush(Color.White))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(brush, 0, 0, 41, 41);
                string initials = String.IsNullOrEmpty(name) ? "?" : name.Substring(0, Math.Min(2, name.Length));
                SizeF size = g.MeasureString(initials, font);
                g.DrawString(initials, font, white, (42 - size.Width) / 2, (42 - size.Height) / 2);
            }
            return bitmap;
        }
    }

    internal static class ResidueScanner
    {
        private static DateTime sizeScanDeadline;
        internal static string[] ProtectedPaths { get { return ProtectedPathPolicy.Paths; } }

        internal static List<CleanupCandidate> Scan(InstalledPackage package, out List<string> observations, out bool protectedConflict)
        {
            sizeScanDeadline = DateTime.UtcNow.AddSeconds(3);
            var results = new List<CleanupCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var locations = new List<string>();
            var publishers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            observations = new List<string>();
            protectedConflict = false;
            package.QuietUninstallString = "";
            package.UninstallString = "";
            package.UninstallLocations = new List<string>();
            bool ambiguousQuiet = false;
            bool ambiguousUninstall = false;
            string wanted = Normalize(package.Name);
            string[] uninstallKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            RegistryKey[] roots = { Registry.LocalMachine, Registry.CurrentUser };
            foreach (RegistryKey root in roots)
                foreach (string keyPath in uninstallKeys)
                {
                    try
                    {
                        using (RegistryKey uninstall = root.OpenSubKey(keyPath))
                        {
                            if (uninstall == null) continue;
                            foreach (string keyName in uninstall.GetSubKeyNames())
                            {
                                try
                                {
                                    using (RegistryKey app = uninstall.OpenSubKey(keyName))
                                    {
                                        if (app == null || Normalize(Convert.ToString(app.GetValue("DisplayName"))) != wanted) continue;
                                        string publisher = Convert.ToString(app.GetValue("Publisher"));
                                        if (!String.IsNullOrWhiteSpace(publisher)) publishers.Add(publisher.Trim());
                                        string quiet = Convert.ToString(app.GetValue("QuietUninstallString"));
                                        string uninstallCommand = Convert.ToString(app.GetValue("UninstallString"));
                                        if (!String.IsNullOrWhiteSpace(quiet))
                                        {
                                            if (String.IsNullOrWhiteSpace(package.QuietUninstallString)) package.QuietUninstallString = quiet;
                                            else if (!package.QuietUninstallString.Equals(quiet, StringComparison.OrdinalIgnoreCase)) ambiguousQuiet = true;
                                        }
                                        if (!String.IsNullOrWhiteSpace(uninstallCommand))
                                        {
                                            if (String.IsNullOrWhiteSpace(package.UninstallString)) package.UninstallString = uninstallCommand;
                                            else if (!package.UninstallString.Equals(uninstallCommand, StringComparison.OrdinalIgnoreCase)) ambiguousUninstall = true;
                                        }
                                        AddLocation(locations, Convert.ToString(app.GetValue("InstallLocation")));
                                        string icon = Convert.ToString(app.GetValue("DisplayIcon"));
                                        if (!String.IsNullOrWhiteSpace(icon))
                                        {
                                            icon = Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"'));
                                            int comma = icon.LastIndexOf(',');
                                            if (comma > 0) icon = icon.Substring(0, comma).Trim().Trim('"');
                                            if (File.Exists(icon)) AddLocation(locations, Path.GetDirectoryName(icon));
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

            if (ambiguousQuiet) package.QuietUninstallString = "";
            if (ambiguousUninstall) package.UninstallString = "";
            package.UninstallLocations.AddRange(locations);

            foreach (string location in locations)
            {
                if (IsProtected(location))
                {
                    protectedConflict = true;
                    observations.Add("停止项：登记安装位置与本机保护路径重叠，自动卸载已禁用：" + location);
                }
                AddCandidate(results, seen, "空目录探测（仅空时移除）", location, "EmptyDirectory", false, false, true, "卸载登记 InstallLocation: " + location);
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            List<string> dataFolderNames = GetDataFolderNames(package);
            string[] rootsToCheck = { local, roaming, programData };
            foreach (string dataRoot in rootsToCheck)
            {
                foreach (string productName in dataFolderNames)
                {
                    string nameEvidence = Normalize(productName) == Normalize(package.Name) ? "数据目录名与显示名称精确匹配" : "数据目录名与 Winget 包 ID 末段精确匹配: " + package.Id;
                    AddCandidate(results, seen, "应用数据（可能含设置或作品）", Path.Combine(dataRoot, productName), "DeleteDirectory", false, true, false, nameEvidence);
                    foreach (string publisher in publishers)
                        if (IsSimpleLeafName(publisher)) AddCandidate(results, seen, "应用数据（可能含设置或作品）", Path.Combine(Path.Combine(dataRoot, publisher), productName), "DeleteDirectory", false, true, false, "Publisher 与 " + nameEvidence);
                }
            }

            string[] menuRoots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ""),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };
            foreach (string menuRoot in menuRoots)
            {
                try
                {
                    if (!Directory.Exists(menuRoot)) continue;
                    foreach (string file in Directory.GetFiles(menuRoot, "*.lnk", SearchOption.TopDirectoryOnly))
                        if (Normalize(Path.GetFileNameWithoutExtension(file)) == wanted)
                            AddCandidate(results, seen, "开始菜单快捷方式", file, "DeleteFile", true, false, true, "快捷方式文件名与软件名称匹配");
                    foreach (string directory in Directory.GetDirectories(menuRoot, "*", SearchOption.TopDirectoryOnly))
                        if (Normalize(Path.GetFileName(directory)) == wanted)
                            AddCandidate(results, seen, "开始菜单空目录探测", directory, "EmptyDirectory", false, false, true, "目录名与软件名称匹配；只删除空目录");
                }
                catch { }
            }

            AddRuntimeObservations(package, locations, observations, results, seen);
            AddProductRegistryCandidates(package, publishers, results, seen);
            results.Sort(delegate(CleanupCandidate a, CleanupCandidate b) { int tier = a.Tier.CompareTo(b.Tier); return tier != 0 ? tier : StringComparer.CurrentCultureIgnoreCase.Compare(a.Category + a.Path, b.Category + b.Path); });
            return results;
        }

        internal static List<string> GetDataFolderNames(InstalledPackage package)
        {
            var names = new List<string>();
            Action<string> add = delegate(string value)
            {
                if (!IsSimpleLeafName(value)) return;
                foreach (string existing in names) if (existing.Equals(value, StringComparison.OrdinalIgnoreCase)) return;
                names.Add(value);
            };
            add(package == null ? "" : package.Name == null ? "" : package.Name.Trim());
            string id = package == null ? "" : package.Id;
            if (!String.IsNullOrEmpty(id))
            {
                int lastDot = id.LastIndexOf('.');
                if (lastDot >= 0 && lastDot < id.Length - 1) add(id.Substring(lastDot + 1));
            }
            return names;
        }

        private static void AddRuntimeObservations(InstalledPackage package, List<string> locations, List<string> observations, List<CleanupCandidate> results, HashSet<string> seen)
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string executable = process.MainModule == null ? "" : process.MainModule.FileName;
                    if (WithinAny(executable, locations) && !IsProtected(executable))
                        AddActionCandidate(results, seen, "KillProcess", "结束进程（高危）", executable, "PID: " + process.Id + " · 完整路径: " + executable, false, process.Id, "", "", "", "");
                }
                catch { }
                finally { process.Dispose(); }
            }

            try
            {
                using (RegistryKey services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (services != null)
                        foreach (string serviceName in services.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey service = services.OpenSubKey(serviceName))
                                {
                                    string image = Convert.ToString(service == null ? null : service.GetValue("ImagePath"));
                                    string executable = ExtractExecutable(Environment.ExpandEnvironmentVariables(image ?? ""));
                                    if (!WithinAny(executable, locations) || IsProtected(executable)) continue;
                                    string evidence = @"HKLM\SYSTEM\CurrentControlSet\Services\" + serviceName + @"\ImagePath = " + image;
                                    string serviceKey = @"SYSTEM\CurrentControlSet\Services\" + serviceName;
                                    AddActionCandidate(results, seen, "StopService", "停止服务（高危）", serviceName, "ImagePath: " + image + " · " + evidence, false, 0, "HKLM", serviceKey, "ImagePath", image);
                                    AddActionCandidate(results, seen, "DeleteService", "删除服务（高危）", serviceName, "ImagePath: " + image + " · " + evidence, false, 0, "HKLM", serviceKey, "ImagePath", image);
                                }
                            }
                            catch { }
                        }
                }
            }
            catch { }

            string[] runKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"
            };
            foreach (RegistryKey root in new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (string runPath in runKeys)
                {
                    try
                    {
                        using (RegistryKey run = root.OpenSubKey(runPath))
                        {
                            if (run == null) continue;
                            foreach (string valueName in run.GetValueNames())
                            {
                                string raw = Convert.ToString(run.GetValue(valueName)) ?? "";
                                if (!WithinAny(ExtractExecutable(Environment.ExpandEnvironmentVariables(raw)), locations)) continue;
                                string hive = root == Registry.LocalMachine ? "HKLM" : "HKCU";
                                string evidence = hive + @"\" + runPath + @"\" + valueName + " = " + raw;
                                AddActionCandidate(results, seen, "DeleteRegistryValue", "删除自启动项（高危）", valueName, "注册表值路径: " + evidence, false, 0, hive, runPath, valueName, raw);
                            }
                        }
                    }
                    catch { }
                }

            if (observations.Count == 0 && locations.Count == 0) observations.Add("未找到卸载登记中的安装目录，因此无法可靠匹配运行进程、服务或自启动项。");
        }

        private static void AddProductRegistryCandidates(InstalledPackage package, HashSet<string> publishers, List<CleanupCandidate> results, HashSet<string> seen)
        {
            string wanted = Normalize(package.Name);
            string[] softwareRoots = { @"SOFTWARE", @"SOFTWARE\WOW6432Node" };
            foreach (RegistryKey root in new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine })
                foreach (string softwarePath in softwareRoots)
                {
                    try
                    {
                        using (RegistryKey software = root.OpenSubKey(softwarePath))
                        {
                            if (software == null) continue;
                            foreach (string vendor in software.GetSubKeyNames())
                            {
                                if (Normalize(vendor) == wanted && !IsGenericRegistryName(vendor))
                                {
                                    string keyPath = softwarePath + @"\" + vendor;
                                    string hive = root == Registry.LocalMachine ? "HKLM" : "HKCU";
                                    AddActionCandidate(results, seen, "DeleteRegistryKey", "删除产品注册表键（高危）", vendor, "名称与软件名匹配 · 完整键路径: " + hive + @"\" + keyPath, false, 0, hive, keyPath, "", "");
                                }
                                if (!publishers.Contains(vendor)) continue;
                                try
                                {
                                    using (RegistryKey publisherKey = software.OpenSubKey(vendor))
                                    {
                                        if (publisherKey == null) continue;
                                        foreach (string product in publisherKey.GetSubKeyNames())
                                            if (Normalize(product) == wanted && !IsGenericRegistryName(product))
                                            {
                                                string keyPath = softwarePath + @"\" + vendor + @"\" + product;
                                                string hive = root == Registry.LocalMachine ? "HKLM" : "HKCU";
                                                AddActionCandidate(results, seen, "DeleteRegistryKey", "删除产品注册表键（高危）", product, "Publisher 与软件名匹配 · 完整键路径: " + hive + @"\" + keyPath, false, 0, hive, keyPath, "", "");
                                            }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
        }

        private static bool IsGenericRegistryName(string value)
        {
            string name = Normalize(value);
            return name == "microsoft" || name == "windows" || name == "classes" || name == "policies" || name == "run" || name == "uninstall" || name == "wow6432node";
        }

        private static void AddActionCandidate(List<CleanupCandidate> results, HashSet<string> seen, string actionKind, string category, string path, string evidence, bool defaultChecked, int processId, string hive, string keyPath, string valueName, string rawValue)
        {
            string unique = actionKind + "|" + (hive ?? "") + "|" + (keyPath ?? "") + "|" + (valueName ?? "") + "|" + processId + "|" + (path ?? "");
            if (!seen.Add(unique)) return;
            results.Add(new CleanupCandidate
            {
                ActionKind = actionKind, Category = category, Path = path, Label = path, Evidence = evidence,
                CheckedByDefault = defaultChecked, IsHighRisk = true, Tier = 2, ProcessId = processId,
                RegistryHive = hive, RegistryKeyPath = keyPath, RegistryValueName = valueName,
                RegistryValueData = rawValue,
                ServiceName = actionKind == "StopService" || actionKind == "DeleteService" ? path : ""
            });
        }

        private static string ExtractExecutable(string command)
        {
            if (String.IsNullOrWhiteSpace(command)) return "";
            command = command.Trim();
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : "";
            }
            Match match = Regex.Match(command, @"^(.+?\.exe)(?:\s|$)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : command.Split(new[] { ' ' }, 2)[0];
        }

        internal static bool HasSilentSwitch(string command)
        {
            return !String.IsNullOrWhiteSpace(command) && Regex.IsMatch(command, @"(?:^|\s)(?:/S|/SILENT|/VERYSILENT|/QN|/QUIET|--SILENT)(?=\s|$)", RegexOptions.IgnoreCase);
        }

        internal static UninstallerPlan ChooseUninstaller(InstalledPackage package)
        {
            string executable, arguments;
            if (HasSilentSwitch(package.QuietUninstallString) && TryParseRegisteredCommand(package, package.QuietUninstallString, out executable, out arguments))
                return new UninstallerPlan { Executable = executable, Arguments = arguments, IsQuiet = true, Description = "QuietUninstallString 含明确静默参数；将按该官方登记命令运行。" };
            if (TryParseRegisteredCommand(package, package.UninstallString, out executable, out arguments))
                return new UninstallerPlan { Executable = executable, Arguments = arguments, IsQuiet = false, Description = "使用普通 UninstallString，可见卸载界面。" };
            return new UninstallerPlan { UseWinget = true, IsQuiet = false, Description = "未找到可安全使用的普通卸载字符串；改用 Winget 默认卸载模式（不传静默参数）。" };
        }

        private static bool TryParseRegisteredCommand(InstalledPackage package, string command, out string executable, out string arguments)
        {
            executable = "";
            arguments = "";
            if (String.IsNullOrWhiteSpace(command)) return false;
            command = Environment.ExpandEnvironmentVariables(command.Trim());
            Match match = Regex.Match(command, "^\\s*(?:\"([^\"]+)\"|(.+?\\.exe))(?=\\s|$)(.*)$", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            string rawExe = !String.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
            arguments = match.Groups[3].Value.Trim();
            if (Path.GetFileName(rawExe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(arguments, @"(?:^|\s)/I(?:\s|\{)", RegexOptions.IgnoreCase) && !Regex.IsMatch(arguments, @"(?:^|\s)/X(?:\s|\{)", RegexOptions.IgnoreCase)) return false;
                executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
                return File.Exists(executable);
            }
            try { executable = Path.GetFullPath(rawExe); } catch { executable = ""; return false; }
            return Path.IsPathRooted(rawExe) && File.Exists(executable) && WithinAny(executable, package.UninstallLocations);
        }

        private static void AddLocation(List<string> locations, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            try
            {
                string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                bool found = false;
                foreach (string existing in locations) if (existing.Equals(full, StringComparison.OrdinalIgnoreCase)) found = true;
                if (Directory.Exists(full) && !found) locations.Add(full);
            }
            catch { }
        }

        private static void AddCandidate(List<CleanupCandidate> results, HashSet<string> seen, string category, string path, string actionKind, bool isFile, bool isUserData, bool selectedByDefault, string evidence)
        {
            try
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string unique = actionKind + "|" + full;
                if (full.Length < 4 || IsProtected(full) || IsTooBroad(full) || !(isFile ? File.Exists(full) : Directory.Exists(full)) || !seen.Add(unique)) return;
                if (!isFile && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
                bool estimated = false;
                long size = isFile ? SafeFileSize(full) : actionKind == "DeleteDirectory" ? DirectorySize(full, out estimated) : 0;
                results.Add(new CleanupCandidate
                {
                    Category = category, Path = full, IsFile = isFile, IsUserData = isUserData,
                    CheckedByDefault = selectedByDefault && !isUserData,
                    ActionKind = actionKind, Evidence = evidence, Tier = isUserData ? 3 : 1,
                    Label = isFile ? Path.GetFileName(full) : Path.GetFileName(full),
                    Size = size, SizeIsEstimate = estimated
                });
            }
            catch { }
        }

        private static bool IsSimpleLeafName(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            if (Path.IsPathRooted(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.IndexOf(Path.DirectorySeparatorChar) >= 0 || value.IndexOf(Path.AltDirectorySeparatorChar) >= 0) return false;
            return true;
        }

        private static bool IsTooBroad(string path)
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };
            foreach (string root in roots)
                if (!String.IsNullOrWhiteSpace(root))
                    try { if (Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Equals(path, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            return false;
        }

        internal static bool IsProtected(string candidate)
        {
            return ProtectedPathPolicy.IsProtected(candidate);
        }

        private static bool SameOrBelow(string path, string root)
        {
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool WithinAny(string path, List<string> locations)
        {
            path = ExtractExecutable(path);
            try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
            catch { return false; }
            foreach (string location in locations)
                if (SameOrBelow(path, location)) return true;
            return false;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? "").ToLowerInvariant(), "[^\\p{L}\\p{N}]", "");
        }

        private static long SafeFileSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        private static long DirectorySize(string root, out bool estimated)
        {
            long total = 0;
            estimated = false;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && DateTime.UtcNow < sizeScanDeadline)
            {
                string current = pending.Pop();
                try
                {
                    foreach (string file in Directory.GetFiles(current))
                        try { total += new FileInfo(file).Length; } catch { }
                    foreach (string directory in Directory.GetDirectories(current))
                        try { if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory); } catch { }
                }
                catch { }
            }
            estimated = pending.Count > 0;
            return total;
        }

        internal static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        internal static bool ContainsReparsePoint(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                try
                {
                    foreach (string directory in Directory.GetDirectories(current))
                    {
                        FileAttributes attributes = File.GetAttributes(directory);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                        pending.Push(directory);
                    }
                }
                catch { return true; }
            }
            return false;
        }

        internal static List<string> ProtectedPathStatus()
        {
            var result = new List<string>();
            if (ProtectedPathPolicy.ConfigurationFailed) result.Add("本机保护路径配置无法读取；已按安全策略拦截清理。");
            else if (ProtectedPaths.Length == 0) result.Add("未配置额外的本机保护路径。");
            foreach (string path in ProtectedPaths) result.Add((Directory.Exists(path) ? "保留路径存在：" : "保留路径当前不存在：") + path);
            return result;
        }

        internal static bool ValidateProtection()
        {
            string[] roots = { @"C:\Protected Sample" };
            return ProtectedPathPolicy.IsProtected(@"C:\Protected Sample\Game", roots) &&
                ProtectedPathPolicy.IsProtected(@"C:\Protected Sample", roots) &&
                !ProtectedPathPolicy.IsProtected(@"C:\Protected Sample2\Game", roots) &&
                !ProtectedPathPolicy.IsProtected(@"C:\Unrelated\Folder", roots);
        }

        internal static bool ValidateDataFolderAliases()
        {
            List<string> names = GetDataFolderNames(new InstalledPackage { Name = "Notepad++ (x64)", Id = "Notepad++.Notepad++" });
            return names.Count == 2 && names.Contains("Notepad++ (x64)") && names.Contains("Notepad++");
        }
    }

    internal sealed class DeepUninstallDialog : Form
    {
        private readonly CheckedListBox candidatesList;
        private readonly List<CleanupCandidate> candidates;
        private readonly InstalledPackage package;

        internal DeepUninstallDialog(InstalledPackage selected, List<CleanupCandidate> found, List<string> observations, bool protectedConflict, UninstallerPlan uninstallPlan)
        {
            package = selected;
            candidates = found;
            Text = "深度卸载预览";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(860, 650);
            MinimumSize = new Size(700, 520);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(248, 249, 250);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(18), BackColor = BackColor };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "深度卸载预览：" + package.Name, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), ForeColor = Color.FromArgb(27, 39, 58), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            layout.Controls.Add(new Label { Text = "流程：侦察 → 清单确认 → 处理所选进程/服务并运行卸载器 → 清理 → 复查。\r\n卸载方式：" + uninstallPlan.Description, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(145, 91, 30), TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            candidatesList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true, IntegralHeight = false, Font = new Font("Microsoft YaHei UI", 8.5F), BackColor = Color.White };
            layout.Controls.Add(candidatesList, 0, 2);
            long selectedBytes = 0;
            foreach (CleanupCandidate candidate in candidates)
            {
                string tier = candidate.Tier == 1 ? "第一档" : candidate.Tier == 2 ? "第二档·高危" : "第三档·数据";
                string line = tier + " · " + candidate.Category + " · " + (candidate.SizeIsEstimate ? "约 " : "") + ResidueScanner.FormatSize(candidate.Size) + " · " + candidate.Path + " · 证据: " + candidate.Evidence;
                candidatesList.Items.Add(line, candidate.CheckedByDefault);
                if (candidate.CheckedByDefault) selectedBytes += candidate.Size;
            }
            if (candidates.Count == 0)
            {
                candidatesList.Items.Add("未发现可安全识别的安装残留或快捷方式；仍可继续运行官方卸载。", false);
                candidatesList.Enabled = false;
            }
            string observationText = observations.Count == 0 ? "第二档高危候选已在上方清单逐项列出；未额外发现无法归属的软件进程、服务或自启项。" : String.Join(Environment.NewLine, observations.ToArray());
            if (protectedConflict) observationText += Environment.NewLine + "为保护本机路径，本次自动卸载不可继续。";
            var findings = new TextBox { Text = observationText, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(95, 107, 120), BackColor = BackColor, BorderStyle = BorderStyle.None, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            layout.Controls.Add(findings, 0, 3);
            var footer = new Panel { Dock = DockStyle.Fill };
            layout.Controls.Add(footer, 0, 4);
            var estimate = new Label { Text = "勾选项目预计释放：" + ResidueScanner.FormatSize(selectedBytes), Left = 0, Top = 4, AutoSize = true, ForeColor = Color.FromArgb(27, 39, 58), Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            candidatesList.ItemCheck += delegate
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    long bytes = 0;
                    for (int i = 0; i < candidates.Count; i++) if (candidatesList.GetItemChecked(i)) bytes += candidates[i].Size;
                    estimate.Text = "勾选项目预计释放：" + ResidueScanner.FormatSize(bytes);
                });
            };
            footer.Controls.Add(estimate);
            var cancel = new Button { Text = "返回", Width = 90, Height = 34, Left = 0, Top = 3, DialogResult = DialogResult.Cancel };
            footer.Controls.Add(cancel);
            var proceed = new Button { Text = "继续卸载", Width = 128, Height = 34, Left = 0, Top = 3, DialogResult = DialogResult.OK, BackColor = Color.FromArgb(25, 29, 34), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            if (protectedConflict) { proceed.Enabled = false; proceed.Text = "受保护路径冲突"; proceed.Width = 170; }
            footer.Controls.Add(proceed);
            footer.Resize += delegate { proceed.Left = footer.ClientSize.Width - proceed.Width; cancel.Left = proceed.Left - cancel.Width - 10; };
            AcceptButton = proceed;
            CancelButton = cancel;
        }

        internal List<CleanupCandidate> SelectedCandidates()
        {
            var selected = new List<CleanupCandidate>();
            for (int i = 0; i < candidates.Count; i++) if (candidatesList.GetItemChecked(i)) selected.Add(candidates[i]);
            return selected;
        }
    }

    internal sealed class UninstallForm : Form
    {
        private readonly Color ink = Color.FromArgb(27, 39, 58);
        private readonly Color muted = Color.FromArgb(119, 133, 153);
        private readonly Color teal = Color.FromArgb(0, 157, 143);
        private readonly FlowLayoutPanel list;
        private readonly TextBox searchBox;
        private readonly Label status;
        private readonly Label count;
        private readonly Button refreshButton;
        private readonly ProgressBar progress;
        private readonly BackgroundWorker worker;
        private readonly BackgroundWorker iconWorker;
        private readonly List<InstalledPackage> packages;
        private readonly StartupData startupData;
        private readonly LoadingVisual loadingVisual;
        private readonly Timer animationTimer;
        private Timer hideLoadingTimer;
        private readonly Stopwatch scanClock = new Stopwatch();
        private readonly TableLayoutPanel contentSlots;
        private readonly FrostCardPanel detailPanel;
        private readonly Label detailName;
        private readonly Label detailId;
        private readonly Label detailVersion;
        private readonly Label detailSource;
        private readonly PillButton uninstallAction;
        private InstalledPackage selectedPackage;
        private string lastLog = "";
        private InstalledPackage pendingPackage;
        private bool busy;
        private bool scanInProgress;
        private bool lastScanHadError;

        public UninstallForm() : this(null) { }

        internal UninstallForm(StartupData loadedData)
        {
            startupData = loadedData;
            packages = startupData == null ? new List<InstalledPackage>() : startupData.InstalledPackages;
            Text = "Lucky 软件卸载";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(880, 580);
            Size = new Size(1000, 690);
            BackColor = Color.White;
            BackgroundImage = VisualTheme.LoadLandscape();
            BackgroundImageLayout = ImageLayout.Stretch;
            DoubleBuffered = true;
            Font = new Font("Microsoft YaHei UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            root.Controls.Add(header, 0, 0);
            header.Controls.Add(new Label { Text = "卸载软件", Left = 32, Top = 10, AutoSize = true, ForeColor = ink, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold) });
            header.Controls.Add(new Label { Text = "选择图标查看信息；卸载前会再次确认", Left = 35, Top = 50, AutoSize = true, ForeColor = muted, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F) });

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(32, 14, 32, 22), BackColor = Color.Transparent };
            root.Controls.Add(body, 0, 1);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 146F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            body.Controls.Add(layout);

            var searchPanel = new FrostCardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0, 0, 0, 6) };
            layout.Controls.Add(searchPanel, 0, 0);
            searchPanel.Controls.Add(new Label { Text = "搜索", Dock = DockStyle.Left, Width = 54, TextAlign = ContentAlignment.MiddleLeft, ForeColor = muted, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) });
            searchBox = new TextBox { Left = 58, Top = 56, Width = 370, Height = 27, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 9F) };
            searchPanel.Controls.Add(searchBox);
            searchBox.TextChanged += delegate { ApplyFilter(); };
            var clearSearch = new Button { Text = "清空", Left = 438, Top = 55, Width = 58, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(230, 236, 243), ForeColor = ink, Font = new Font("Microsoft YaHei UI", 8.5F), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            clearSearch.Click += delegate { searchBox.Clear(); searchBox.Focus(); };
            searchPanel.Controls.Add(clearSearch);

            var summary = new FrostCardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 12, 0), Margin = new Padding(0, 0, 0, 8) };
            layout.Controls.Add(summary, 0, 1);
            status = new Label { Text = "正在准备已安装软件列表…", Dock = DockStyle.Left, Width = 680, TextAlign = ContentAlignment.MiddleLeft, ForeColor = muted };
            summary.Controls.Add(status);
            count = new Label { Text = "", Dock = DockStyle.Right, Width = 130, TextAlign = ContentAlignment.MiddleRight, ForeColor = teal, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            summary.Controls.Add(count);

            contentSlots = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            contentSlots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            contentSlots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0F));
            contentSlots.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.Controls.Add(contentSlots, 0, 2);
            list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Color.Transparent, Padding = new Padding(8), Margin = new Padding(0) };
            contentSlots.Controls.Add(list, 0, 0);
            detailPanel = new FrostCardPanel { Left = 540, Top = 0, Width = 280, Height = 146, Padding = new Padding(10), Visible = false };
            searchPanel.Resize += delegate { detailPanel.Left = Math.Max(516, Math.Min(820 - detailPanel.Width, searchPanel.ClientSize.Width - detailPanel.Width - 12)); };
            searchPanel.Controls.Add(detailPanel);
            var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            detailPanel.Controls.Add(details);
            detailName = new Label { Dock = DockStyle.Fill, ForeColor = ink, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            detailId = new Label { Dock = DockStyle.Fill, ForeColor = muted, BackColor = Color.Transparent, Font = new Font("Consolas", 8.5F), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            detailVersion = new Label { Dock = DockStyle.Fill, ForeColor = ink, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 10F), TextAlign = ContentAlignment.MiddleLeft };
            detailSource = new Label { Dock = DockStyle.Fill, ForeColor = muted, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 8.5F), TextAlign = ContentAlignment.TopLeft };
            details.Controls.Add(detailName, 0, 0); details.Controls.Add(detailId, 0, 1); details.Controls.Add(detailVersion, 0, 2); details.Controls.Add(detailSource, 0, 3);
            uninstallAction = new PillButton { Text = "深度卸载", Dock = DockStyle.Fill, Margin = new Padding(1, 1, 1, 1), Enabled = false };
            uninstallAction.Click += delegate { if (selectedPackage != null) Uninstall(selectedPackage); };
            details.Controls.Add(uninstallAction, 0, 4);

            var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(244, 247, 251), Margin = new Padding(0) };
            layout.Controls.Add(footer, 0, 3);
            refreshButton = MakeButton("刷新列表", 112, Color.FromArgb(230, 236, 243), ink);
            refreshButton.Left = 0;
            refreshButton.Top = 4;
            refreshButton.Click += delegate { Scan(); };
            footer.Controls.Add(refreshButton);
            var logButton = MakeButton("查看日志", 100, Color.FromArgb(230, 236, 243), ink);
            logButton.Left = 122;
            logButton.Top = 4;
            logButton.Click += ShowLog;
            footer.Controls.Add(logButton);
            progress = new ProgressBar { Dock = DockStyle.Right, Width = 170, Height = 12, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false, Margin = new Padding(0, 17, 0, 0) };
            footer.Controls.Add(progress);

            worker = new BackgroundWorker();
            worker.DoWork += WorkerDoWork;
            worker.RunWorkerCompleted += WorkerCompleted;
            iconWorker = new BackgroundWorker { WorkerReportsProgress = true, WorkerSupportsCancellation = true };
            iconWorker.DoWork += LoadPackageIcons;
            iconWorker.ProgressChanged += ApplyPackageIcon;
            iconWorker.RunWorkerCompleted += PackageIconsCompleted;
            loadingVisual = new LoadingVisual { Dock = DockStyle.Fill, Stage = "正在准备卸载清单…" };
            Controls.Add(loadingVisual);
            loadingVisual.Visible = startupData == null || !startupData.IconsLoaded;
            if (loadingVisual.Visible) loadingVisual.BringToFront();
            animationTimer = new Timer { Interval = 80 };
            animationTimer.Tick += delegate
            {
                if (!loadingVisual.Visible) return;
                if (scanInProgress)
                {
                    int crawlProgress = Math.Min(45, (int)(scanClock.ElapsedMilliseconds * 45 / 60000));
                    if (crawlProgress > loadingVisual.Progress) loadingVisual.Progress = crawlProgress;
                }
                loadingVisual.Frame++;
                loadingVisual.Invalidate();
            };
            Shown += delegate { animationTimer.Start(); };
            FormClosed += delegate
            {
                animationTimer.Stop();
                animationTimer.Dispose();
                if (hideLoadingTimer != null) { hideLoadingTimer.Stop(); hideLoadingTimer.Dispose(); hideLoadingTimer = null; }
                if (iconWorker.IsBusy) iconWorker.CancelAsync();
            };
            if (startupData != null)
            {
                startupData.IconUpdated += UpdatePackageIcon;
                Disposed += delegate { startupData.IconUpdated -= UpdatePackageIcon; };
            }
            Load += delegate
            {
                if (startupData == null) Scan();
                else
                {
                    lastLog = startupData.InstalledScan == null ? "" : startupData.InstalledScan.Output + (String.IsNullOrEmpty(startupData.InstalledScan.Error) ? "" : Environment.NewLine + startupData.InstalledScan.Error);
                    List<InstalledPackage> readyPackages = new List<InstalledPackage>(packages);
                    foreach (InstalledPackage package in readyPackages) AddRow(package, false);
                    status.Text = startupData.InstalledScan != null && startupData.InstalledScan.ExitCode != 0 ? "已显示启动时读取的项目；刷新可重新扫描。" : "已使用启动时准备好的软件清单和图标。";
                    count.Text = packages.Count + " 项";
                    ApplyFilter();
                    if (!startupData.IconsLoaded) CompleteLoading();
                }
            };
        }

        private void UpdatePackageIcon(string id, Image image)
        {
            if (IsDisposed || Disposing) return;
            foreach (InstalledPackage package in packages)
                if (String.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase) && package.IconBox != null && !package.IconBox.IsDisposed)
                    MainForm.ReplacePictureIcon(package.IconBox, image);
        }

        private Label Column(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = muted, Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold) };
        }

        private static Button MakeButton(string text, int width, Color back, Color fore)
        {
            return new Button { Text = text, Width = width, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        }

        private void BeginLoading(string stage)
        {
            if (hideLoadingTimer != null) { hideLoadingTimer.Stop(); hideLoadingTimer.Dispose(); hideLoadingTimer = null; }
            loadingVisual.Progress = 0;
            loadingVisual.Stage = stage;
            loadingVisual.Visible = true;
            loadingVisual.BringToFront();
            scanClock.Restart();
            scanInProgress = true;
            animationTimer.Start();
            loadingVisual.Invalidate();
        }

        private void UpdateLoading(int progressValue, string stage)
        {
            loadingVisual.Progress = Math.Max(loadingVisual.Progress, Math.Max(0, Math.Min(99, progressValue)));
            loadingVisual.Stage = stage;
            loadingVisual.Invalidate();
        }

        private void CompleteLoading(string completionStage = "卸载清单和图标已准备好")
        {
            scanInProgress = false;
            loadingVisual.Progress = 100;
            loadingVisual.Stage = completionStage;
            loadingVisual.Invalidate();
            if (hideLoadingTimer != null) { hideLoadingTimer.Stop(); hideLoadingTimer.Dispose(); }
            hideLoadingTimer = new Timer { Interval = 320 };
            hideLoadingTimer.Tick += delegate
            {
                hideLoadingTimer.Stop();
                hideLoadingTimer.Dispose();
                hideLoadingTimer = null;
                animationTimer.Stop();
                if (!IsDisposed && !Disposing) loadingVisual.Visible = false;
            };
            hideLoadingTimer.Start();
        }

        private void Scan()
        {
            if (busy) return;
            busy = true;
            BeginLoading("正在扫描已安装软件…");
            refreshButton.Enabled = false;
            progress.Visible = true;
            status.Text = "正在通过 Winget 查询已安装的软件…只扫描，不会卸载。";
            count.Text = "";
            ClearSelection();
            foreach (InstalledPackage old in packages)
                if (old.IconBox != null && old.IconBox.Image != null) old.IconBox.Image.Dispose();
            list.Controls.Clear();
            packages.Clear();
            worker.RunWorkerAsync("scan");
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            bool scanning = e.Argument is string && (string)e.Argument == "scan";
            if (scanning)
            {
                CommandResult result = MainForm.RunWinget("list --disable-interactivity");
                e.Result = new object[] { "scan", result, ParseInstalled(result.Output) };
            }
            else
            {
                UninstallJob job = (UninstallJob)e.Argument;
                InstalledPackage package = job.Package;
                List<string> protectedBefore = ResidueScanner.ProtectedPathStatus();
                var cleanupReport = new List<string>();
                var actionResults = new Dictionary<CleanupCandidate, string>();
                long freed = 0;
                cleanupReport.Add("3/5 官方卸载方式：" + job.Uninstaller.Description);
                foreach (CleanupCandidate candidate in job.Cleanup)
                    if (candidate.ActionKind == "KillProcess" || candidate.ActionKind == "StopService")
                        actionResults[candidate] = MainForm.PerformPreUninstallAction(candidate);

                CommandResult result = MainForm.RunSelectedUninstaller(job);
                cleanupReport.Add("官方卸载退出代码：" + result.ExitCode);
                foreach (CleanupCandidate candidate in job.Cleanup)
                {
                    if (!actionResults.ContainsKey(candidate))
                    {
                        if (result.ExitCode != 0) actionResults[candidate] = "未执行：官方卸载未成功";
                        else
                        {
                            long itemFreed;
                            actionResults[candidate] = MainForm.PerformPostUninstallAction(candidate, out itemFreed);
                            freed += itemFreed;
                        }
                    }
                }
                foreach (CleanupCandidate candidate in job.AllCandidates)
                {
                    string actionStatus;
                    if (!job.Cleanup.Contains(candidate)) actionStatus = "保留（未勾选）";
                    else actionStatus = actionResults.ContainsKey(candidate) ? actionResults[candidate] : "未执行";
                    cleanupReport.Add(actionStatus + " · " + candidate.Category + " · " + candidate.Path + " · 证据: " + candidate.Evidence);
                    cleanupReport.Add("复查: " + MainForm.VerifyCandidate(candidate));
                }
                List<string> protectedAfter = ResidueScanner.ProtectedPathStatus();
                e.Result = new object[] { "uninstall", result, package, cleanupReport, freed, protectedBefore, protectedAfter };
            }
        }

        private void LoadPackageIcons(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker background = (BackgroundWorker)sender;
            List<InstalledPackage> packagesToLoad = (List<InstalledPackage>)e.Argument;
            for (int i = 0; i < packagesToLoad.Count; i++)
            {
                if (background.CancellationPending) { e.Cancel = true; return; }
                InstalledPackage package = packagesToLoad[i];
                Image icon = null;
                try { icon = MainForm.ResolveAppIcon(package.Name, package.Id); }
                catch { }
                if (background.CancellationPending)
                {
                    if (icon != null) icon.Dispose();
                    e.Cancel = true;
                    return;
                }
                background.ReportProgress(0, Tuple.Create(package, icon, i + 1, packagesToLoad.Count));
            }
        }

        private void ApplyPackageIcon(object sender, ProgressChangedEventArgs e)
        {
            Tuple<InstalledPackage, Image, int, int> update = e.UserState as Tuple<InstalledPackage, Image, int, int>;
            if (update == null) return;
            try
            {
                if (IsDisposed || Disposing) return;
                InstalledPackage package = update.Item1;
                if (update.Item2 != null && package.IconBox != null && !package.IconBox.IsDisposed)
                {
                    if (startupData != null) startupData.SetIcon(package.Id, update.Item2);
                    else MainForm.ReplacePictureIcon(package.IconBox, update.Item2);
                }
                int progressValue = 50 + (int)(49.0 * update.Item3 / Math.Max(1, update.Item4));
                UpdateLoading(progressValue, "正在加载软件图标… " + update.Item3 + " / " + update.Item4);
            }
            finally
            {
                if (update.Item2 != null) update.Item2.Dispose();
            }
        }

        private void PackageIconsCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (IsDisposed || Disposing) return;
            busy = false;
            refreshButton.Enabled = true;
            progress.Visible = false;
            uninstallAction.Enabled = selectedPackage != null && IsSafePackageId(selectedPackage.Id);
            if (e.Error != null) status.Text = "图标读取有部分失败；仍可使用已加载的软件清单。";
            else if (lastScanHadError) status.Text = "清单部分读取失败；可查看日志，已显示能识别的项目。";
            else if (packages.Count == 0) status.Text = "没有识别到可管理的软件，或 Winget 输出格式暂不兼容。";
            else status.Text = "扫描和图标加载完成。这里只显示 Winget 识别到的软件。";
            CompleteLoading(e.Error != null || lastScanHadError ? "清单不完整，请查看日志" : "卸载清单和图标已准备好");
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            busy = false;
            refreshButton.Enabled = true;
            progress.Visible = false;
            uninstallAction.Enabled = selectedPackage != null && IsSafePackageId(selectedPackage.Id);
            if (e.Error != null)
            {
                lastLog = e.Error.ToString();
                status.Text = "操作失败：" + e.Error.Message;
                if (pendingPackage != null) pendingPackage = null;
                if (scanInProgress) CompleteLoading("清单读取失败，请查看日志");
                return;
            }

            object[] resultData = (object[])e.Result;
            string mode = (string)resultData[0];
            CommandResult result = (CommandResult)resultData[1];
            lastLog = result.Output + (String.IsNullOrEmpty(result.Error) ? "" : Environment.NewLine + result.Error);
            if (mode == "scan")
            {
                List<InstalledPackage> found = (List<InstalledPackage>)resultData[2];
                lastScanHadError = result.ExitCode != 0;
                scanInProgress = false;
                foreach (InstalledPackage package in found) AddRow(package);
                ApplyFilter();
                if (result.ExitCode != 0)
                {
                    status.Text = "列表读取未完全成功；正在整理已识别项目。";
                    count.Text = "";
                }
                else if (found.Count == 0)
                {
                    status.Text = "没有识别到可管理的软件，或 Winget 输出格式暂不兼容。";
                    count.Text = "0 项";
                }
                else
                {
                    status.Text = "正在读取软件图标…";
                    count.Text = found.Count + " 项";
                }
                if (found.Count == 0)
                {
                    busy = false;
                    refreshButton.Enabled = true;
                    progress.Visible = false;
                    if (result.ExitCode != 0) status.Text = "列表读取失败（代码 " + result.ExitCode + "）。可查看日志。";
                    CompleteLoading(result.ExitCode != 0 ? "清单读取失败，请查看日志" : "没有可加载的项目");
                }
                else
                {
                    busy = true;
                    refreshButton.Enabled = false;
                    progress.Visible = true;
                    UpdateLoading(50, "已读取软件清单，正在加载软件图标…");
                    iconWorker.RunWorkerAsync(new List<InstalledPackage>(found));
                }
            }
            else
            {
                InstalledPackage package = (InstalledPackage)resultData[2];
                pendingPackage = null;
                if (result.ExitCode == 0)
                {
                    List<string> cleanupReport = (List<string>)resultData[3];
                    List<string> before = (List<string>)resultData[5];
                    List<string> after = (List<string>)resultData[6];
                    var report = new StringBuilder();
                    report.AppendLine(package.Name + "：官方卸载命令已返回成功。" + Environment.NewLine);
                    foreach (string entry in cleanupReport) report.AppendLine(entry);
                    report.AppendLine();
                    report.AppendLine("保护路径复查：");
                    bool protectedUnchanged = before.Count == after.Count;
                    for (int i = 0; i < Math.Min(before.Count, after.Count); i++)
                    {
                        bool same = before[i].StartsWith("保留路径存在：", StringComparison.Ordinal) == after[i].StartsWith("保留路径存在：", StringComparison.Ordinal);
                        if (!same) protectedUnchanged = false;
                        report.AppendLine((same ? "✓ " : "⚠ ") + after[i]);
                    }
                    long freed = (long)resultData[4];
                    report.AppendLine();
                    report.AppendLine("由本工具删除的项目预计释放：" + ResidueScanner.FormatSize(freed));
                    report.AppendLine(protectedUnchanged ? "保护路径状态未改变。" : "保护路径状态发生变化，请检查。清理程序未将其列为目标。");
                    lastLog += Environment.NewLine + report.ToString();
                    status.Text = package.Name + "：卸载及残留清理已完成，正在刷新列表…";
                    lastLog += Environment.NewLine + report.ToString();
                    ShowDeepReport(report.ToString(), result.ExitCode == 0 && protectedUnchanged);
                    if (result.ExitCode == 0) Scan();
                    else status.Text = package.Name + "：官方卸载失败，所选清理项未执行；请查看复查报告。";
                }
                else
                {
                    List<string> cleanupReport = (List<string>)resultData[3];
                    var report = new StringBuilder();
                    report.AppendLine(package.Name + "：官方卸载失败（代码 " + result.ExitCode + "）。");
                    if (!String.IsNullOrWhiteSpace(result.Error)) report.AppendLine(result.Error);
                    report.AppendLine();
                    foreach (string entry in cleanupReport) report.AppendLine(entry);
                    report.AppendLine(); report.AppendLine("保护路径复查：");
                    List<string> before = (List<string>)resultData[5];
                    List<string> after = (List<string>)resultData[6];
                    for (int i = 0; i < Math.Min(before.Count, after.Count); i++) report.AppendLine(after[i]);
                    lastLog += Environment.NewLine + report.ToString();
                    status.Text = package.Name + " 卸载未完成（代码 " + result.ExitCode + "）；已跳过后续清理。";
                    ShowDeepReport(report.ToString(), false);
                }
            }
        }

        private void ShowDeepReport(string text, bool success)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = success ? "深度卸载复查" : "深度卸载结果（有项目未完成）";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(900, 620);
                var report = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F), Text = text };
                dialog.Controls.Add(report);
                dialog.ShowDialog(this);
            }
        }

        private void AddRow(InstalledPackage package, bool addToCollection = true)
        {
            if (addToCollection) packages.Add(package);
            Panel row = new Panel { Width = 100, Height = 112, BackColor = Color.Transparent, Margin = new Padding(4), Cursor = Cursors.Hand };
            package.Row = row;
            PictureBox icon = new PictureBox { Left = 22, Top = 3, Width = 56, Height = 56, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Cursor = Cursors.Hand };
            Image cachedIcon;
            if (startupData != null && startupData.IconCache.TryGetValue(StartupData.IconKey(package.Id), out cachedIcon) && cachedIcon != null)
                icon.Image = new Bitmap(cachedIcon);
            else
                icon.Image = MainForm.MakeFallbackIcon(package.Name, package.Id);
            package.IconBox = icon;
            var name = new Label { Text = package.Name, Left = 0, Top = 64, Width = 100, Height = 42, ForeColor = ink, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 8.5F), AutoEllipsis = true, TextAlign = ContentAlignment.TopCenter, Cursor = Cursors.Hand };
            row.Controls.Add(icon); row.Controls.Add(name);
            row.Click += delegate { SelectPackage(package); };
            icon.Click += delegate { SelectPackage(package); };
            name.Click += delegate { SelectPackage(package); };
            package.UninstallButton = uninstallAction;
            uninstallAction.Enabled = false;
            list.Controls.Add(row);
        }

        private void SelectPackage(InstalledPackage package)
        {
            selectedPackage = package;
            detailName.Text = package.Name;
            detailId.Text = package.Id;
            detailVersion.Text = "已安装版本  " + package.Version;
            detailSource.Text = "来源  " + (String.IsNullOrEmpty(package.Source) ? "未标明" : package.Source) + " · 深度卸载会先预览清理项";
            detailPanel.Visible = true;
            uninstallAction.Enabled = !busy && IsSafePackageId(package.Id);
        }

        private void ClearSelection()
        {
            selectedPackage = null;
            if (detailPanel != null) detailPanel.Visible = false;
            if (contentSlots != null && contentSlots.ColumnStyles.Count > 1) contentSlots.ColumnStyles[1].Width = 0F;
            if (uninstallAction != null) uninstallAction.Enabled = false;
        }

        private void Uninstall(InstalledPackage package)
        {
            if (busy || !IsSafePackageId(package.Id)) return;
            List<string> observations;
            bool protectedConflict;
            List<CleanupCandidate> candidates = ResidueScanner.Scan(package, out observations, out protectedConflict);
            UninstallerPlan uninstallPlan = ResidueScanner.ChooseUninstaller(package);
            var preview = new DeepUninstallDialog(package, candidates, observations, protectedConflict, uninstallPlan);
            List<CleanupCandidate> selectedCandidates;
            using (preview)
            {
                if (preview.ShowDialog(this) != DialogResult.OK) return;
                selectedCandidates = preview.SelectedCandidates();
            }
            int highRiskCount = selectedCandidates.Count(delegate(CleanupCandidate candidate) { return candidate.IsHighRisk; });
            string message = "将执行五步卸载流程。选中的第二档高危操作有 " + highRiskCount + " 项；目录/注册表删除不可撤销。\r\n\r\n卸载方式：" + uninstallPlan.Description + "\r\n软件：" + package.Name + "\r\n版本：" + package.Version + "\r\n包 ID：" + package.Id + "\r\n来源：" + (String.IsNullOrEmpty(package.Source) ? "未标明" : package.Source) + "\r\n\r\n第三档数据目录默认不勾选，可能含设置、存档或作品。是否继续？";
            if (MessageBox.Show(this, message, "确认深度卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            busy = true;
            pendingPackage = package;
            refreshButton.Enabled = false;
            foreach (InstalledPackage entry in packages)
                if (entry.UninstallButton != null) entry.UninstallButton.Enabled = false;
            uninstallAction.Enabled = false;
            progress.Visible = true;
            status.Text = "正在卸载并清理 " + package.Name + "…";
            worker.RunWorkerAsync(new UninstallJob { Package = package, Cleanup = selectedCandidates, AllCandidates = candidates, Uninstaller = uninstallPlan });
        }

        private void ApplyFilter()
        {
            string query = searchBox == null ? "" : searchBox.Text.Trim();
            int shown = 0;
            foreach (InstalledPackage package in packages)
            {
                bool match = String.IsNullOrEmpty(query) || (package.Name + " " + package.Id + " " + package.Source).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                if (package.Row != null) package.Row.Visible = match;
                if (match) shown++;
            }
            if (selectedPackage != null && !MatchesPackage(selectedPackage, query)) ClearSelection();
            if (!String.IsNullOrEmpty(query)) count.Text = shown + " / " + packages.Count + " 项";
            else if (packages.Count > 0) count.Text = packages.Count + " 项";
        }

        private static bool MatchesPackage(InstalledPackage package, string query)
        {
            return String.IsNullOrEmpty(query) || (package.Name + " " + package.Id + " " + package.Source + " " + package.Version).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ResizeRows() { }

        private void ShowLog(object sender, EventArgs e)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "Winget 卸载界面日志";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(820, 480);
                dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F), Text = String.IsNullOrEmpty(lastLog) ? "暂无日志。" : lastLog });
                dialog.ShowDialog(this);
            }
        }

        private static bool IsSafePackageId(string id)
        {
            return PackageIdPolicy.IsSafeId(id);
        }

        internal static List<InstalledPackage> ParseInstalled(string output)
        {
            var result = new List<InstalledPackage>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = (output ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("-")) continue;
                string[] parts = Regex.Split(line, "\\s{2,}");
                if (parts.Length < 3) continue;
                string name = parts[0].Trim();
                string id = parts[1].Trim();
                if (name.Equals("Name", StringComparison.OrdinalIgnoreCase) || name.Equals("名称", StringComparison.Ordinal) || name.Equals("名称", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsSafePackageId(id) || seen.Contains(id)) continue;
                string source = parts.Length >= 5 ? parts[4].Trim() : "";
                if (parts.Length == 4 && !Regex.IsMatch(parts[3].Trim(), "^[0-9]+([._+-][A-Za-z0-9]+)*$")) source = parts[3].Trim();
                result.Add(new InstalledPackage { Name = name, Id = id, Version = parts[2].Trim(), Source = source });
                seen.Add(id);
            }
            return result;
        }

        internal static List<InstalledPackage> ParseForStartup(string output) { return ParseInstalled(output); }

        internal static bool ValidateInstalledParser()
        {
            string sample = "Name                Id                                      Version       Available     Source\r\n" +
                "------------------  --------------------------------------  ------------  ------------  ------\r\n" +
                "微信                Tencent.WeChat                           4.0.0         4.1.0         winget\r\n" +
                "Sample Desktop App  Vendor.Sample                            1.2.3                       winget\r\n" +
                "Legacy App          ARP\\Machine\\X64\\{12345678-1234-1234-1234-123456789012}  1.0.0\r\n" +
                "Notepad++           Notepad++.Notepad++                      8.8.1\r\n";
            List<InstalledPackage> parsed = ParseInstalled(sample);
            return parsed.Count == 4 && parsed[0].Name == "微信" && parsed[0].Source == "winget" && parsed[1].Id == "Vendor.Sample" && parsed[1].Source == "winget" && parsed[2].Id.StartsWith("ARP\\", StringComparison.Ordinal) && parsed[3].Id == "Notepad++.Notepad++";
        }
    }

    internal static class VisualTheme
    {
        internal static Image LoadLandscape()
        {
            using (Stream stream = typeof(VisualTheme).Assembly.GetManifestResourceStream("LuckyUpdater.LuckyBackground.png"))
            {
                if (stream == null) return null;
                using (Image image = Image.FromStream(stream)) return new Bitmap(image);
            }
        }
    }

    internal sealed class FrostCardPanel : Panel
    {
        public FrostCardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Parent != null)
            {
                Rectangle r = new Rectangle(Left, Top, Width, Height);
                e.Graphics.TranslateTransform(-Left, -Top);
                PaintEventArgs pe = new PaintEventArgs(e.Graphics, r);
                InvokePaintBackground(Parent, pe);
                InvokePaint(Parent, pe);
                e.Graphics.TranslateTransform(Left, Top);
            }
            using (var path = RoundedRectangle(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 18))
            using (var fill = new SolidBrush(Color.FromArgb(222, 255, 255, 255)))
            using (var border = new Pen(Color.FromArgb(190, 255, 255, 255), 1F))
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }

        internal static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(Rectangle rect, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class PillButton : Button
    {
        public PillButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.FromArgb(25, 29, 34);
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            int diameter = Math.Min(r.Height, r.Width / 2);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            using (var brush = new SolidBrush(Enabled ? BackColor : Color.FromArgb(140, 140, 140)))
            using (var textBrush = new SolidBrush(Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                path.StartFigure();
                path.AddLine(r.Left + diameter / 2, r.Top, r.Right - diameter / 2, r.Top);
                path.AddArc(r.Right - diameter, r.Top, diameter, diameter, 270F, 180F);
                path.AddLine(r.Right - diameter / 2, r.Bottom, r.Left + diameter / 2, r.Bottom);
                path.AddArc(r.Left, r.Top, diameter, diameter, 90F, 180F);
                path.CloseFigure();
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawString(Text, Font, textBrush, r, format);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                string sample = "Name  Id  Version  Available  Source\r\n----  --  -------  ---------  ------\r\n" +
                    "Sample App  Sample.Publisher  1.0.0  1.1.0  winget\r\n" +
                    "Outlook  Microsoft.Outlook  16.0  17.0  msstore\r\n" +
                    "Store App  9N1234567890  2.0  2.1  msstore\r\n" +
                    "Notepad++  Notepad++.Notepad++  8.8.0  8.8.1  winget\r\n";
                List<UpdateItem> parsed = MainForm.ParseForSelfTest(sample);
                bool parserOk = parsed.Count == 4 && parsed[0].Id == "Sample.Publisher" && parsed[1].Source == "msstore" && parsed[2].Id == "9N1234567890" && parsed[3].Id == "Notepad++.Notepad++" && PackageIdPolicy.IsWingetId("Notepad++.Notepad++") && !PackageIdPolicy.IsWingetId("Bad/Package") && PackageIdPolicy.IsSafeId("Vendor:App") && PackageIdPolicy.IsSafeId(@"ARP\Machine\X64\{ABC-123}");
                bool warningMatchOk = MainForm.IsWorkBuddy(new UpdateItem { Name = "WorkBuddy", Id = "WorkBuddy.WorkBuddy" }) && MainForm.IsWorkBuddy(new UpdateItem { Name = "Other name", Id = "Vendor.WorkBuddy" }) && !MainForm.IsWorkBuddy(new UpdateItem { Name = "Other App", Id = "Vendor.OtherApp" });
                bool quietPolicyOk = ResidueScanner.HasSilentSwitch("setup.exe /S") && ResidueScanner.HasSilentSwitch("setup.exe /SILENT") && ResidueScanner.HasSilentSwitch("msiexec.exe /qn") && !ResidueScanner.HasSilentSwitch("setup.exe /repair") && !ResidueScanner.HasSilentSwitch("setup.exe /SOMETHING");
                Environment.Exit(parserOk && warningMatchOk && quietPolicyOk && MainForm.ValidateRowLayout() && MainForm.ValidateSearchFilter() && UninstallForm.ValidateInstalledParser() && ResidueScanner.ValidateProtection() && ResidueScanner.ValidateDataFolderAliases() ? 0 : 1);
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var context = new ApplicationContext())
            using (var splash = new StartupSplash())
            {
                MainForm mainForm = null;
                context.MainForm = splash;
                splash.PrepareMainWindow = delegate(StartupData data)
                {
                    mainForm = new MainForm(data);
                    mainForm.FormClosed += delegate { if (data != null) data.Dispose(); };
                    context.MainForm = mainForm;
                    mainForm.Show();
                };
                splash.Show();
                Application.Run(context);
                if (mainForm != null && !mainForm.IsDisposed) mainForm.Dispose();
                if (mainForm == null && splash.Data != null) splash.Data.Dispose();
            }
        }
    }
}
