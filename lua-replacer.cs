using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Net;

namespace StormworksLuaReplacer
{
    public class ApplicationState
    {
        public bool IsReloading { get; set; }
        public string ScriptDetectionPrefix { get; set; } = "-- autochanger";
        public Point MouseLocation { get; set; }
        public bool IsDarkTheme { get; set; } = true;
        public bool SuppressPrefixPrompt { get; set; } = false;
        public string SettingsFilePath { get; set; } = string.Empty;
        public List<string> RecentFiles { get; set; } = new List<string>();
    }

    public class PrefixConfirmDialog : Form
    {
        private CheckBox chkDontShow;
        public bool DontShowAgain => chkDontShow?.Checked ?? false;
        public PrefixConfirmDialog(List<string> displayNames)
        {
            this.Text = "プレフィックスを追加します";
            this.Size = new System.Drawing.Size(520, 320);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var lbl = new Label { Text = "以下の置換されたスクリプトに検出プレフィックスを追加します。続行しますか？", Dock = DockStyle.Top, Height = 36, Padding = new Padding(8) };
            var txt = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new System.Drawing.Font("Consolas", 9) };
            txt.Text = string.Join(Environment.NewLine, displayNames);
            chkDontShow = new CheckBox { Text = "今後表示しない", Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(6, 4, 0, 4) };
            var btnOk = new Button { Text = "はい", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
            var btnCancel = new Button { Text = "いいえ", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
            var pnlButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(6) };
            pnlButtons.Controls.Add(btnCancel);
            pnlButtons.Controls.Add(btnOk);

            this.Controls.Add(txt);
            this.Controls.Add(lbl);
            this.Controls.Add(chkDontShow);
            this.Controls.Add(pnlButtons);
            this.AcceptButton = btnOk;
            this.CancelButton = btnCancel;
        }
    }

    public partial class MainForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private Point resizeStart;
        private Rectangle resizeStartBounds;
        private bool isResizing = false;
        private int resizeMode = 0;
        private XDocument? vehicleXml;
        private string? currentFilePath;
        private string? currentLuaFilePath;
        private readonly List<LuaScriptNode> luaScripts = new List<LuaScriptNode>();
        private readonly FileSystemWatcher fileWatcher;
        private readonly FileSystemWatcher luaFileWatcher;
        private readonly ApplicationState appState = new ApplicationState();
        private bool suppressMessages = false;
        private bool isHttpRequest = false;
        private HttpListener? httpListener;

        private Label? lblFilePath;
        private ModernDropdown? cbRecentFiles;
        private ListBox? lstScripts;
        private TextBox? txtCurrentScript;
        private TextBox? txtNewScript;
        private Panel? pnlTitleBar;
        private Panel? titleRightSpacer;
        private Panel? pnlCurrentBorder;
        private Panel? pnlNewBorder;
        private CustomVScroll? vScrollList;
        private CustomVScroll? vScrollCurrent;
        private CustomVScroll? vScrollNew;
        // status strip removed to avoid interfering with window resizing
        private Color accentColor = Color.FromArgb(0, 122, 204);
        
        

        private const int SB_VERT = 1;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private const int WM_VSCROLL = 0x0115;
        private const int SB_PAGEUP = 2;
        private const int SB_PAGEDOWN = 3;
        private const int SB_BOTTOM = 7;

        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private const int RESIZE_BORDER = 8;
        private System.Windows.Forms.Timer? reloadTimer;
        private System.Windows.Forms.Timer? luaReloadTimer;

        // Global keyboard hook
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private IntPtr _hookID = IntPtr.Zero;
        private LowLevelKeyboardProc _proc;

        public MainForm()
        {
            InitializeComponent();

            this.KeyPreview = true;

            this.KeyDown += MainForm_KeyDown;

            // Install global keyboard hook
            _proc = HookCallback;
            _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

            LoadSettings();
            // Populate recent-files dropdown after settings are loaded
            UpdateRecentCombo();
            // Apply theme from loaded settings (call again to ensure colors update)
            ApplyTheme();
            this.DoubleBuffered = true;
            this.MinimumSize = new Size(400, 300);
            AttachMouseHandlers(this);
            fileWatcher = new FileSystemWatcher { NotifyFilter = NotifyFilters.LastWrite };
            fileWatcher.SynchronizingObject = this;
            fileWatcher.Changed += FileWatcher_Changed;
            fileWatcher.Created += FileWatcher_Changed;
            fileWatcher.Deleted += FileWatcher_Changed;
            fileWatcher.Renamed += FileWatcher_Changed;

            luaFileWatcher = new FileSystemWatcher { NotifyFilter = NotifyFilters.LastWrite };
            luaFileWatcher.SynchronizingObject = this;
            luaFileWatcher.Changed += LuaFileWatcher_Changed;
            luaFileWatcher.Created += LuaFileWatcher_Changed;
            luaFileWatcher.Deleted += LuaFileWatcher_Changed;
            luaFileWatcher.Renamed += LuaFileWatcher_Changed;

            // Start HTTP server
            Task.Run(() => StartHttpServer());
        }

        private async Task StartHttpServer()
        {
            httpListener = new HttpListener();
            httpListener.Prefixes.Add("http://localhost:2345/");
            httpListener.Start();
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await httpListener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    // Listener was stopped or suffered an error - exit loop
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Listener disposed - exit loop
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Listener not in a valid state - exit loop
                    break;
                }

                var request = context.Request;
                var response = context.Response;
                if (request.Url.AbsolutePath == "/replace" && request.HttpMethod == "GET")
                {
                    string? errorMessage = null;
                    // UI スレッド上での処理を非同期に実行し、その完了を待つ。
                    var asyncResult = this.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            isHttpRequest = true;
                            if (vehicleXml == null || lstScripts!.SelectedIndex < 0)
                            {
                                errorMessage = "XML file is not loaded or no script is selected.";
                                return;
                            }
                            suppressMessages = true;
                            BtnReplace_Click(null, EventArgs.Empty);
                            BtnSaveSync();
                            suppressMessages = false;
                        }
                        catch (Exception ex)
                        {
                            errorMessage = ex.Message;
                        }
                        finally
                        {
                            isHttpRequest = false;
                        }
                    }));

                    try
                    {
                        // UI スレッド側の処理が終わるまで待機（HTTP スレッドをブロックするが、UI はブロックされない）
                        this.EndInvoke(asyncResult);
                    }
                    catch (Exception ex)
                    {
                        // EndInvoke による例外はここで拾っておく
                        if (string.IsNullOrEmpty(errorMessage)) errorMessage = ex.Message;
                    }

                    string responseString;
                    if (string.IsNullOrEmpty(errorMessage))
                    {
                        // 成功: Lua 側の判定が期待するパターンを含める
                        var payload = new { status = "success", message = "Files updated successfully." };
                        responseString = System.Text.Json.JsonSerializer.Serialize(payload);
                    }
                    else
                    {
                        // 失敗: success パターンを含めないようにする
                        var reason = TranslateErrorToEnglish(errorMessage);
                        var payload = new { status = "error", reason = reason };
                        responseString = System.Text.Json.JsonSerializer.Serialize(payload);
                    }
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                    response.ContentType = "text/plain; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                else
                {
                    response.StatusCode = 404;
                }
                try
                {
                    response.OutputStream.Close();
                }
                catch { }
            }
        }

        private async Task BtnSaveAsync()
        {
            if (vehicleXml == null || string.IsNullOrEmpty(currentFilePath)) { throw new Exception("XMLファイルが読み込まれていません。"); }
            await SaveXmlFileAsync(currentFilePath);
        }

        private void BtnSaveSync()
        {
            if (vehicleXml == null || string.IsNullOrEmpty(currentFilePath)) { throw new Exception("XMLファイルが読み込まれていません。"); }
            SaveXmlFileAsync(currentFilePath).Wait();
        }

        private void AttachMouseHandlers(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                c.MouseDown += ChildControl_MouseDown;
                c.MouseMove += ChildControl_MouseMove;
                c.MouseUp += ChildControl_MouseUp;
                if (c.HasChildren) AttachMouseHandlers(c);
            }
        }

        private void ChildControl_MouseDown(object? sender, MouseEventArgs e){
            var ctrl = sender as Control;
            if (ctrl == null) return;
            var screenPt = ctrl.PointToScreen(e.Location);
            var formPt = this.PointToClient(screenPt);
            int mode = GetResizeMode(formPt);
            if (mode != HTCLIENT)
            {
                isResizing = true;
                resizeMode = mode;
                resizeStart = screenPt;
                resizeStartBounds = this.Bounds;
                ctrl.Capture = true;
            }
        }

        private void ChildControl_MouseMove(object? sender, MouseEventArgs e)
        {
            var ctrl = sender as Control;
            if (ctrl == null) return;
            var screenPt = ctrl.PointToScreen(e.Location);
            if (isResizing)
            {
                ResizeWindow(screenPt);
                return;
            }
            var formPt = this.PointToClient(screenPt);
            int mode = GetResizeMode(formPt);
            UpdateCursor(mode);
        }

        private void ChildControl_MouseUp(object? sender, MouseEventArgs e)
        {
            if (isResizing)
            {
                isResizing = false;
                resizeMode = HTCLIENT;
                this.Cursor = Cursors.Default;
                var ctrl = sender as Control;
                if (ctrl != null) ctrl.Capture = false;
            }
        }

        private void ResizeWindow(Point currentScreenLocation)
        {
            if (!isResizing) return;
            int deltaX = currentScreenLocation.X - resizeStart.X;
            int deltaY = currentScreenLocation.Y - resizeStart.Y;
            const int MIN_WIDTH = 400;
            const int MIN_HEIGHT = 300;
            int newLeft = resizeStartBounds.Left;
            int newTop = resizeStartBounds.Top;
            int newWidth = resizeStartBounds.Width;
            int newHeight = resizeStartBounds.Height;
            bool isLeft = (resizeMode == HTLEFT || resizeMode == HTTOPLEFT || resizeMode == HTBOTTOMLEFT);
            bool isRight = (resizeMode == HTRIGHT || resizeMode == HTTOPRIGHT || resizeMode == HTBOTTOMRIGHT);
            bool isTop = (resizeMode == HTTOP || resizeMode == HTTOPLEFT || resizeMode == HTTOPRIGHT);
            bool isBottom = (resizeMode == HTBOTTOM || resizeMode == HTBOTTOMLEFT || resizeMode == HTBOTTOMRIGHT);
            if (isLeft)
            {
                int proposedWidth = resizeStartBounds.Width - deltaX;
                if (proposedWidth < MIN_WIDTH) proposedWidth = MIN_WIDTH;
                newWidth = proposedWidth;
                newLeft = (resizeStartBounds.Left + resizeStartBounds.Width) - newWidth;
            }
            else if (isRight)
            {
                newWidth = resizeStartBounds.Width + deltaX;
                if (newWidth < MIN_WIDTH) newWidth = MIN_WIDTH;
            }
            if (isTop)
            {
                int proposedHeight = resizeStartBounds.Height - deltaY;
                if (proposedHeight < MIN_HEIGHT) proposedHeight = MIN_HEIGHT;
                newHeight = proposedHeight;
                newTop = (resizeStartBounds.Top + resizeStartBounds.Height) - newHeight;
            }
            else if (isBottom)
            {
                newHeight = resizeStartBounds.Height + deltaY;
                if (newHeight < MIN_HEIGHT) newHeight = MIN_HEIGHT;
            }
            this.Bounds = new Rectangle(newLeft, newTop, newWidth, newHeight);
        }

        private int GetResizeMode(Point clientPoint)
        {
            bool left = clientPoint.X <= RESIZE_BORDER;
            bool right = clientPoint.X >= this.ClientSize.Width - RESIZE_BORDER;
            bool top = clientPoint.Y <= RESIZE_BORDER;
            bool bottom = clientPoint.Y >= this.ClientSize.Height - RESIZE_BORDER;
            if (left && top) return HTTOPLEFT;
            if (right && top) return HTTOPRIGHT;
            if (left && bottom) return HTBOTTOMLEFT;
            if (right && bottom) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return HTCLIENT;
        }

        private void UpdateCursor(int mode)
        {
            // If window is maximized, do not show resize cursors
            if (this.WindowState == FormWindowState.Maximized)
            {
                this.Cursor = Cursors.Default;
                return;
            }
            switch (mode)
            {
                case HTLEFT:
                case HTRIGHT:
                    this.Cursor = Cursors.SizeWE;
                    break;
                case HTTOP:
                case HTBOTTOM:
                    this.Cursor = Cursors.SizeNS;
                    break;
                case HTTOPLEFT:
                case HTBOTTOMRIGHT:
                    this.Cursor = Cursors.SizeNWSE;
                    break;
                case HTTOPRIGHT:
                case HTBOTTOMLEFT:
                    this.Cursor = Cursors.SizeNESW;
                    break;
                default:
                    this.Cursor = Cursors.Default;
                    break;
            }
        }

        private void InitializeComponent()
        {
            lblFilePath = new Label { Text = "ファイル: 未選択", Dock = DockStyle.Fill, AutoSize = false, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            lstScripts = new ListBox { Dock = DockStyle.Fill, Height = 300 };
            txtCurrentScript = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new System.Drawing.Font("Consolas", 10), ReadOnly = true, BorderStyle = BorderStyle.None };
            txtNewScript = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new System.Drawing.Font("Consolas", 10), BorderStyle = BorderStyle.None };
            this.FormBorderStyle = FormBorderStyle.None;
            this.Text = "";
            pnlTitleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = System.Drawing.Color.FromArgb(45, 45, 48)
            };
            var lblTitle = new Label { Text = "Stormworks Lua Script Replacer", ForeColor = System.Drawing.Color.White, Location = new System.Drawing.Point(10, 8) };
            lblTitle.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            this.Font = new System.Drawing.Font("Segoe UI", 9F);
            var btnMaximize = new Button { Text = "🗖", Dock = DockStyle.Right, Width = 45, FlatStyle = FlatStyle.Flat, ForeColor = System.Drawing.Color.White, BackColor = System.Drawing.Color.FromArgb(60, 45, 72) };
            btnMaximize.FlatAppearance.BorderSize = 0;
            btnMaximize.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(63, 63, 70);
            var btnMinimize = new Button { Text = "—", Dock = DockStyle.Right, Width = 45, FlatStyle = FlatStyle.Flat, ForeColor = System.Drawing.Color.White, BackColor = System.Drawing.Color.FromArgb(60, 45, 72) };
            var btnClose = new Button { Text = "✕", Dock = DockStyle.Right, Width = 45, FlatStyle = FlatStyle.Flat, ForeColor = System.Drawing.Color.White, BackColor = System.Drawing.Color.FromArgb(60, 45, 72) };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(212, 63, 63);
            btnMinimize.FlatAppearance.BorderSize = 0;
            btnMinimize.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(63, 63, 70);
            pnlTitleBar.Controls.Add(lblTitle);
            pnlTitleBar.Controls.Add(btnMinimize);
            pnlTitleBar.Controls.Add(btnMaximize);
            pnlTitleBar.Controls.Add(btnClose);
            // spacer to the right of titlebar buttons to create a clickable margin
            titleRightSpacer = new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Color.Transparent };
            pnlTitleBar.Controls.Add(titleRightSpacer);
            // hide spacer when window is maximized to avoid extra offset
            titleRightSpacer.Visible = this.WindowState != FormWindowState.Maximized;
            // Ensure maximized bounds use the working area (so the taskbar isn't covered)
            this.Load += (s, e) =>
            {
                try { this.MaximizedBounds = Screen.FromHandle(this.Handle).WorkingArea; }
                catch { }
                if (titleRightSpacer != null) titleRightSpacer.Visible = this.WindowState != FormWindowState.Maximized;
            };
            this.Resize += (s, e) =>
            {
                try { this.MaximizedBounds = Screen.FromHandle(this.Handle).WorkingArea; }
                catch { }
                if (titleRightSpacer != null) titleRightSpacer.Visible = this.WindowState != FormWindowState.Maximized;
            };
            btnClose.Click += (s, e) => this.Close();
            btnMinimize.Click += (s, e) => this.WindowState = FormWindowState.Minimized;
            btnMaximize.Click += (s, e) =>
            {
                this.WindowState = this.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                btnMaximize.Text = this.WindowState == FormWindowState.Maximized ? "🗗" : "🗖";
            };
            pnlTitleBar.MouseDown += (s, e) =>
            {
                var screenPt = pnlTitleBar.PointToScreen(e.Location);
                var formPt = this.PointToClient(screenPt);
                if (GetResizeMode(formPt) != HTCLIENT) return;
                appState.MouseLocation = e.Location;
            };
            pnlTitleBar.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && appState.MouseLocation != Point.Empty)
                {
                    var screenPt = pnlTitleBar.PointToScreen(e.Location);
                    var formPt = this.PointToClient(screenPt);
                    if (GetResizeMode(formPt) != HTCLIENT) return;
                    this.Left += e.X - appState.MouseLocation.X;
                    this.Top += e.Y - appState.MouseLocation.Y;
                }
            };
            lblTitle.MouseDown += (s, e) =>
            {
                var screenPt = lblTitle.PointToScreen(e.Location);
                var formPt = this.PointToClient(screenPt);
                if (GetResizeMode(formPt) != HTCLIENT) return;
                pnlTitleBar.Capture = false;
                Message msg = Message.Create(pnlTitleBar.Handle, 0x00A1, (IntPtr)0x0002, IntPtr.Zero);
                this.DefWndProc(ref msg);
            };
            
            var menuStrip = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("ファイル");
            var openXmlItem = new ToolStripMenuItem("ビークルXMLを開く...", null, BtnLoadXml_Click);
            var saveXmlItem = new ToolStripMenuItem("XMLを保存", null, BtnSave_Click);
            var saveAsXmlItem = new ToolStripMenuItem("名前を付けて保存...", null, BtnSaveAs_Click);
            var exitItem = new ToolStripMenuItem("終了", null, (s, e) => this.Close());
            fileMenu.DropDownItems.AddRange(new ToolStripItem[] { openXmlItem, saveXmlItem, saveAsXmlItem, new ToolStripSeparator(), exitItem });
            var editMenu = new ToolStripMenuItem("編集");
            var loadLuaItem = new ToolStripMenuItem("Luaファイルを読み込む...", null, BtnLoadLuaFile_Click);
            var replaceItem = new ToolStripMenuItem("置換", null, BtnReplace_Click);
            editMenu.DropDownItems.AddRange(new ToolStripItem[] { loadLuaItem, replaceItem });
            var toolsMenu = new ToolStripMenuItem("ツール");
            var settingsItem = new ToolStripMenuItem("設定...", null, BtnSettings_Click);
            var toggleThemeItem = new ToolStripMenuItem("テーマ切替", null, BtnToggleTheme_Click);
            toolsMenu.DropDownItems.AddRange(new ToolStripItem[] { settingsItem, toggleThemeItem });
            ((ToolStripDropDownMenu)fileMenu.DropDown).ShowImageMargin = false;
            ((ToolStripDropDownMenu)editMenu.DropDown).ShowImageMargin = false;
            ((ToolStripDropDownMenu)toolsMenu.DropDown).ShowImageMargin = false;
            menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, toolsMenu });
            // Use professional renderer without rounded corners to remove rounded appearance
            menuStrip.Renderer = new ToolStripProfessionalRenderer();
            if (menuStrip.Renderer is ToolStripProfessionalRenderer msr) msr.RoundedEdges = false;
            var toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            var openXmlBtn = new ToolStripButton("XMLを開く", null, BtnLoadXml_Click) { Margin = new Padding(5, 0, 0, 0) };
            var loadLuaBtn = new ToolStripButton("Lua読込", null, BtnLoadLuaFile_Click);
            var replaceBtn = new ToolStripButton("置換", null, BtnReplace_Click);
            var saveBtn = new ToolStripButton("保存", null, BtnSave_Click);
            var settingsBtn = new ToolStripButton("設定", null, BtnSettings_Click);
            // subtle hover effect: change ForeColor to accent
            foreach (ToolStripButton tsb in new[] { openXmlBtn, saveBtn, loadLuaBtn, replaceBtn, settingsBtn })
            {
                tsb.MouseEnter += (s, e) => tsb.ForeColor = accentColor;
                tsb.MouseLeave += (s, e) => tsb.ForeColor = appState.IsDarkTheme ? Color.FromArgb(230, 230, 230) : Color.Black;
                tsb.DisplayStyle = ToolStripItemDisplayStyle.Text;
            }
            // remove visual separators and use margins for spacing to avoid vertical lines
            loadLuaBtn.Margin = new Padding(10, 1, 0, 2);
            settingsBtn.Margin = new Padding(10, 1, 0, 2);
            toolStrip.Items.AddRange(new ToolStripItem[] { openXmlBtn, loadLuaBtn, replaceBtn, saveBtn, settingsBtn });
            // Use same renderer style as menu to avoid rounded corners
            toolStrip.Renderer = new ToolStripProfessionalRenderer();
            if (toolStrip.Renderer is ToolStripProfessionalRenderer tsr) tsr.RoundedEdges = false;
            this.Text = "Stormworks Lua Script Replacer";
            this.Size = new System.Drawing.Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            lstScripts!.SelectedIndexChanged += LstScripts_SelectedIndexChanged;
            lstScripts.DrawMode = DrawMode.OwnerDrawFixed;
            lstScripts.DrawItem += LstScripts_DrawItem;
            lstScripts.MouseWheel += (s, e) =>
            {
                if (vScrollList == null) return;
                int delta = -e.Delta / 120;
                vScrollList.Value = Math.Max(vScrollList.Minimum, Math.Min(vScrollList.Maximum, vScrollList.Value + delta * Math.Max(1, vScrollList.SmallChange)));
            };
            // Wrap textboxes in thin border panels so we can control border color in dark mode
            pnlCurrentBorder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Tag = "border" };
            pnlCurrentBorder.Controls.Add(txtCurrentScript);
            txtCurrentScript!.MouseWheel += (s, e) =>
            {
                if (txtCurrentScript == null) return;
                int pages = Math.Max(1, Math.Abs(e.Delta) / 120);
                if (e.Delta < 0)
                {
                    for (int i = 0; i < pages; i++) SendMessage(txtCurrentScript.Handle, WM_VSCROLL, (IntPtr)SB_PAGEDOWN, IntPtr.Zero);
                }
                else if (e.Delta > 0)
                {
                    for (int i = 0; i < pages; i++) SendMessage(txtCurrentScript.Handle, WM_VSCROLL, (IntPtr)SB_PAGEUP, IntPtr.Zero);
                }
                UpdateTextScrollbars();
                SyncVScrollFromText(txtCurrentScript, vScrollCurrent);
            };
            txtCurrentScript.TextChanged += (s, e) => UpdateTextScrollbars();
            var grpCurrentScript = new GroupBox { Text = "現在のスクリプト", Dock = DockStyle.Fill };
            grpCurrentScript.Controls.Add(pnlCurrentBorder);

            pnlNewBorder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Tag = "border" };
            pnlNewBorder.Controls.Add(txtNewScript);
            txtNewScript!.MouseWheel += (s, e) =>
            {
                if (txtNewScript == null) return;
                int pages = Math.Max(1, Math.Abs(e.Delta) / 120);
                if (e.Delta < 0)
                {
                    for (int i = 0; i < pages; i++) SendMessage(txtNewScript.Handle, WM_VSCROLL, (IntPtr)SB_PAGEDOWN, IntPtr.Zero);
                }
                else if (e.Delta > 0)
                {
                    for (int i = 0; i < pages; i++) SendMessage(txtNewScript.Handle, WM_VSCROLL, (IntPtr)SB_PAGEUP, IntPtr.Zero);
                }
                UpdateTextScrollbars();
                SyncVScrollFromText(txtNewScript, vScrollNew);
            };
            txtNewScript.TextChanged += (s, e) => UpdateTextScrollbars();
            var grpNewScript = new GroupBox { Text = "新しいスクリプト", Dock = DockStyle.Fill };
            grpNewScript.Controls.Add(pnlNewBorder);
            var mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 300F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            // script list will be placed inside the XML area per user request
            // XML preview area (left column, spans top row)
            var xmlPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6), Tag = "xmlPanel" };
            // Recent-files dropdown (replaces simple file label)
            cbRecentFiles = new ModernDropdown { Dock = DockStyle.Top, Height = 26 };
            cbRecentFiles.SelectedIndexChanged += async (s, e) =>
            {
                if (cbRecentFiles == null) return;
                var sel = cbRecentFiles.SelectedItem;
                if (string.IsNullOrWhiteSpace(sel)) return;
                if (string.Equals(sel, currentFilePath, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    currentFilePath = sel;
                    await LoadXmlFileAsync();
                    SetupFileWatcher();
                    AddToRecentFiles(sel);
                }
                catch (Exception ex) { MessageBox.Show($"XMLファイルの読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            var xmlInner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5), Tag = "border" };
            // place the detected scripts list inside the XML area (user requested)
            xmlInner.Controls.Add(lstScripts);
            var grpXml = new GroupBox { Text = "XML ファイル", Dock = DockStyle.Fill };
            grpXml.Controls.Add(xmlInner);
            // Add controls: group (fills), then clear button, then recent-files combobox docked at top
            xmlPanel.Controls.Add(grpXml);
            xmlPanel.Controls.Add(cbRecentFiles!);

            mainLayout.Controls.Add(xmlPanel, 0, 0);
            mainLayout.SetColumnSpan(xmlPanel, 2);
            // create custom scrollbars and add to respective containers
            vScrollList = new CustomVScroll { Dock = DockStyle.Right, Width = 14 };
            // attach scrollbar to the scripts list inside the left XML area
            xmlInner.Controls.Add(vScrollList);
            vScrollList.ValueChanged += (s, e) => { if (lstScripts != null) lstScripts.TopIndex = vScrollList.Value; };

            vScrollCurrent = new CustomVScroll { Dock = DockStyle.Right, Width = 14 };
            pnlCurrentBorder!.Controls.Add(vScrollCurrent);
            vScrollCurrent.ValueChanged += (s, e) =>
            {
                if (txtCurrentScript == null) return;
                int line = vScrollCurrent.Value;
                if (line < 0) line = 0;
                if (line >= txtCurrentScript.Lines.Length) line = Math.Max(0, txtCurrentScript.Lines.Length - 1);
                int idx = txtCurrentScript.GetFirstCharIndexFromLine(line);
                if (idx >= 0)
                {
                    int prevSel = txtCurrentScript.SelectionStart;
                    txtCurrentScript.SelectionStart = idx;
                    txtCurrentScript.ScrollToCaret();
                    txtCurrentScript.SelectionStart = prevSel;
                }
            };

            vScrollNew = new CustomVScroll { Dock = DockStyle.Right, Width = 14 };
            pnlNewBorder!.Controls.Add(vScrollNew);
            vScrollNew.ValueChanged += (s, e) =>
            {
                if (txtNewScript == null) return;
                int line = vScrollNew.Value;
                if (line < 0) line = 0;
                if (line >= txtNewScript.Lines.Length) line = Math.Max(0, txtNewScript.Lines.Length - 1);
                int idx = txtNewScript.GetFirstCharIndexFromLine(line);
                if (idx >= 0)
                {
                    int prevSel = txtNewScript.SelectionStart;
                    txtNewScript.SelectionStart = idx;
                    txtNewScript.ScrollToCaret();
                    txtNewScript.SelectionStart = prevSel;
                }
            };
            mainLayout.Controls.Add(grpCurrentScript, 0, 1);
            mainLayout.Controls.Add(grpNewScript, 1, 1);
            // thin separators between menu/tool/main areas (theme-aware via Tag = "borderline")
            var menuSeparator = new Panel { Dock = DockStyle.Top, Height = 1, Tag = "borderline" };
            var toolSeparator = new Panel { Dock = DockStyle.Top, Height = 1, Tag = "borderline" };
            // Ensure menu/tool dock to top explicitly
            menuStrip.Dock = DockStyle.Top;
            toolStrip.Dock = DockStyle.Top;
            // Add controls in reverse so Dock = Top stacks correctly (last added appears at the top):
            // mainLayout (fills remaining), toolSeparator, toolStrip, menuSeparator, menuStrip, pnlTitleBar (top)
            this.Controls.Add(mainLayout);
            this.Controls.Add(toolSeparator);
            this.Controls.Add(toolStrip);
            this.Controls.Add(menuSeparator);
            this.Controls.Add(menuStrip);
            this.Controls.Add(pnlTitleBar);
            this.MainMenuStrip = menuStrip;
            ApplyTheme();
            // initialize scrollbar ranges / hide native scrollbars
            UpdateListScrollbar();
            UpdateTextScrollbars();
        }

        private void BtnToggleTheme_Click(object? sender, EventArgs e)
        {
            appState.IsDarkTheme = !appState.IsDarkTheme;
            ApplyTheme();
            SaveSettings();
        }

        private void ApplyTheme()
        {
            bool dark = appState.IsDarkTheme;
            // Form base
            this.BackColor = dark ? System.Drawing.Color.FromArgb(37, 37, 38) : SystemColors.Control;
            // Use a slightly off-white for dark-mode text/borders to reduce harsh contrast
            this.ForeColor = dark ? Color.FromArgb(230, 230, 230) : Color.Black;
            // Title bar must remain fixed color
            if (pnlTitleBar != null) pnlTitleBar.BackColor = Color.FromArgb(60, 45, 72);
            // Apply recursively to other controls (skip title bar)
            foreach (Control c in this.Controls)
            {
                if (c == pnlTitleBar) continue;
                ApplyThemeToControl(c, dark);
            }
            // ensure custom scrollbars use theme-friendly colors
            if (vScrollList != null)
            {
                vScrollList.BackColor = dark ? Color.FromArgb(60, 45, 72) : SystemColors.Control;
                vScrollList.ForeColor = dark ? Color.FromArgb(200, 200, 200) : Color.Black;
            }
            if (vScrollCurrent != null)
            {
                vScrollCurrent.BackColor = dark ? Color.FromArgb(60, 45, 72) : SystemColors.Control;
                vScrollCurrent.ForeColor = dark ? Color.FromArgb(200, 200, 200) : Color.Black;
            }
            if (vScrollNew != null)
            {
                vScrollNew.BackColor = dark ? Color.FromArgb(60, 45, 72) : SystemColors.Control;
                vScrollNew.ForeColor = dark ? Color.FromArgb(200, 200, 200) : Color.Black;
            }
            // status strip removed (avoids interfering with window resize)
            // xml editor and line numbers (removed)
            // header and header buttons (removed)
        }

        private void ApplyThemeToControl(Control ctrl, bool dark)
        {
            if (ctrl == pnlTitleBar) return;
            // Determine sibling index for subtle alternating contrast
            int siblingIndex = ctrl.Parent?.Controls.IndexOf(ctrl) ?? 0;
            bool alternate = (siblingIndex % 2 == 0);
            // Default colors
            if (dark)
            {
                // Softer text/border color for dark mode
                ctrl.ForeColor = Color.FromArgb(230, 230, 230);
                if (ctrl is TextBox)
                {
                    ctrl.BackColor = Color.FromArgb(30, 30, 30);
                }
                else if (ctrl is ListBox)
                {
                    ctrl.BackColor = Color.FromArgb(30, 30, 30);
                }
                else if (ctrl is GroupBox)
                {
                    // Use a consistent color for all GroupBoxes so paired panels match
                    ctrl.BackColor = Color.FromArgb(37, 37, 38);
                }
                else if (ctrl is Panel)
                {
                    // Panels used as borders carry Tag == "border"
                    if (ctrl.Tag is string t && t == "border")
                    {
                        // use a soft light-gray border instead of pure white
                        ctrl.BackColor = Color.FromArgb(200, 200, 200);
                    }
                    // Panels used as thin separators carry Tag == "borderline"
                    else if (ctrl.Tag is string t2 && t2 == "borderline")
                    {
                        ctrl.BackColor = dark ? Color.FromArgb(80, 80, 80) : Color.FromArgb(200, 200, 200);
                    }
                    else
                    {
                        ctrl.BackColor = alternate ? Color.FromArgb(45, 45, 48) : Color.FromArgb(50, 50, 53);
                    }
                }
                else
                {
                    ctrl.BackColor = Color.FromArgb(45, 45, 48);
                }
            }
            else
            {
                ctrl.ForeColor = Color.Black;
                if (ctrl is TextBox)
                {
                    ctrl.BackColor = Color.White;
                }
                else if (ctrl is ListBox)
                {
                    ctrl.BackColor = Color.White;
                }
                else if (ctrl is GroupBox)
                {
                    // Use a consistent color for all GroupBoxes in light theme as well
                    ctrl.BackColor = SystemColors.Control;
                }
                else if (ctrl is Panel)
                {
                    ctrl.BackColor = alternate ? SystemColors.Control : SystemColors.ControlLight;
                }
                else
                {
                    ctrl.BackColor = SystemColors.Control;
                }
            }
            // Specific control tweaks
            switch (ctrl)
            {
                case TextBox tb:
                    tb.BackColor = dark ? Color.FromArgb(30, 30, 30) : Color.White;
                    tb.ForeColor = dark ? Color.FromArgb(230, 230, 230) : Color.Black;
                    break;
                case ListBox lb:
                    lb.BackColor = dark ? Color.FromArgb(30, 30, 30) : Color.White;
                    lb.ForeColor = dark ? Color.FromArgb(230, 230, 230) : Color.Black;
                    break;
                case GroupBox gb:
                    // already set above with alternation; ensure ForeColor
                    gb.ForeColor = dark ? Color.FromArgb(200, 200, 200) : Color.Black;
                    break;
                case Label l:
                    l.BackColor = Color.Transparent;
                    l.ForeColor = dark ? Color.FromArgb(230, 230, 230) : Color.Black;
                    break;
                case MenuStrip ms:
                    ms.BackColor = dark ? Color.FromArgb(45, 45, 48) : SystemColors.Control;
                    ms.ForeColor = dark ? Color.FromArgb(230, 230, 230) : Color.Black;
                    break;
                case ToolStrip ts:
                    ts.BackColor = dark ? Color.FromArgb(45, 45, 48) : SystemColors.Control;
                    ts.ForeColor = dark ? Color.FromArgb(230, 230, 230) : Color.Black;
                    // Ensure contained items update their colors as well
                    foreach (ToolStripItem item in ts.Items)
                    {
                        item.ForeColor = dark ? Color.FromArgb(230, 230, 230) : Color.Black;
                        if (item is ToolStripButton b) b.DisplayStyle = ToolStripItemDisplayStyle.Text;
                    }
                    break;
                default:
                    break;
            }
            // Recurse
            foreach (Control child in ctrl.Controls)
            {
                ApplyThemeToControl(child, dark);
            }
        }

        private async void BtnLoadXml_Click(object? sender, EventArgs e)
        {
            using var openFileDialog = new OpenFileDialog { Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*", Title = "ビークルXMLファイルを選択" };
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    currentFilePath = openFileDialog.FileName;
                    await LoadXmlFileAsync();
                    SetupFileWatcher();
                    AddToRecentFiles(currentFilePath);
                    MessageBox.Show($"XMLファイルを読み込みました。\n{luaScripts.Count}個のLuaスクリプトが見つかりました。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"XMLファイルの読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExtractLuaScripts()
        {
            luaScripts.Clear();
            if (vehicleXml == null) return;
            var scriptElements = vehicleXml.Descendants().Where(e => e.Attribute("script")?.Value.Trim().StartsWith(appState.ScriptDetectionPrefix, StringComparison.OrdinalIgnoreCase) ?? false);
            luaScripts.AddRange(scriptElements.Select((element, index) =>
            {
                var scriptAttribute = element.Attribute("script")!;
                var scriptContent = scriptAttribute.Value;
                var lines = scriptContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                string identifier = lines.Length > 0 ? lines[0].Substring(2).Trim() : "Unknown Script";
                if (lines.Length > 1 && lines[1].Trim().StartsWith("--")) identifier += " " + lines[1].Substring(2).Trim();
                return new LuaScriptNode { Element = element, Attribute = scriptAttribute, Index = index + 1, Script = scriptContent, DisplayName = identifier };
            }));
        }

        private void UpdateUI()
        {
            // Update recent-files dropdown selection
            if (cbRecentFiles != null)
            {
                if (!string.IsNullOrWhiteSpace(currentFilePath))
                {
                    if (!appState.RecentFiles.Contains(currentFilePath)) AddToRecentFiles(currentFilePath);
                    cbRecentFiles.SelectedItem = currentFilePath;
                }
                else cbRecentFiles.SelectedItem = null;
            }
            lstScripts!.Items.Clear();
            foreach (var script in luaScripts) lstScripts.Items.Add(script.DisplayName);
            UpdateListScrollbar();
            // XML preview removed; nothing to update in this area
        }

        private void UpdateListScrollbar()
        {
            if (lstScripts == null || vScrollList == null) return;
            int visible = Math.Max(1, lstScripts.ClientSize.Height / Math.Max(1, lstScripts.ItemHeight));
            int max = Math.Max(0, Math.Max(0, lstScripts.Items.Count - visible));
            vScrollList.Minimum = 0;
            vScrollList.Maximum = max;
            vScrollList.LargeChange = visible;
            // set thumb pixel size to match visible items (approx)
            try { vScrollList.ThumbSizePixels = visible * lstScripts.ItemHeight; } catch { vScrollList.ThumbSizePixels = 0; }
            try
            {
                int top = lstScripts.TopIndex;
                vScrollList.Value = Math.Max(vScrollList.Minimum, Math.Min(vScrollList.Maximum, top));
            }
            catch
            {
                vScrollList.Value = Math.Min(vScrollList.Value, vScrollList.Maximum);
            }
            // hide native scrollbar
            try { ShowScrollBar(lstScripts.Handle, SB_VERT, false); } catch { }
        }

        private void UpdateTextScrollbars()
        {
            if (txtCurrentScript != null && vScrollCurrent != null)
            {
                int total = Math.Max(1, txtCurrentScript.Lines.Length);
                int lineHeight = TextRenderer.MeasureText("A", txtCurrentScript.Font).Height;
                int visible = Math.Max(1, txtCurrentScript.ClientSize.Height / lineHeight);
                int max = Math.Max(0, total - visible);
                vScrollCurrent.Minimum = 0;
                vScrollCurrent.Maximum = max;
                vScrollCurrent.LargeChange = visible;
                vScrollCurrent.Value = Math.Min(vScrollCurrent.Value, vScrollCurrent.Maximum);
                try { vScrollCurrent.ThumbSizePixels = visible * lineHeight; } catch { vScrollCurrent.ThumbSizePixels = 0; }
                try { ShowScrollBar(txtCurrentScript.Handle, SB_VERT, false); } catch { }
            }
            if (txtNewScript != null && vScrollNew != null)
            {
                int total = Math.Max(1, txtNewScript.Lines.Length);
                int lineHeight2 = TextRenderer.MeasureText("A", txtNewScript.Font).Height;
                int visible = Math.Max(1, txtNewScript.ClientSize.Height / lineHeight2);
                int max = Math.Max(0, total - visible);
                vScrollNew.Minimum = 0;
                vScrollNew.Maximum = max;
                vScrollNew.LargeChange = visible;
                vScrollNew.Value = Math.Min(vScrollNew.Value, vScrollNew.Maximum);
                try { vScrollNew.ThumbSizePixels = visible * lineHeight2; } catch { vScrollNew.ThumbSizePixels = 0; }
                try { ShowScrollBar(txtNewScript.Handle, SB_VERT, false); } catch { }
            }
        }

        private void SyncVScrollFromText(TextBox? tb, CustomVScroll? vs)
        {
            if (tb == null || vs == null) return;
            try
            {
                var pt = new Point(0, 0);
                int firstChar = tb.GetCharIndexFromPosition(pt);
                int firstLine = Math.Max(0, tb.GetLineFromCharIndex(firstChar));
                vs.Value = Math.Max(vs.Minimum, Math.Min(vs.Maximum, firstLine));
            }
            catch { }
        }

        // Recent-files (MRU) helpers
        private void AddToRecentFiles(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = Path.GetFullPath(path);
                // move to front, keep unique
                appState.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                appState.RecentFiles.Insert(0, path);
                // trim to max (10)
                if (appState.RecentFiles.Count > 10) appState.RecentFiles.RemoveRange(10, appState.RecentFiles.Count - 10);
                SaveSettings();
                UpdateRecentCombo();
            }
            catch { }
        }

        private void UpdateRecentCombo()
        {
            try
            {
                if (cbRecentFiles == null) return;
                cbRecentFiles.UpdateItemsFromList(appState.RecentFiles.ToList());
                if (!string.IsNullOrWhiteSpace(currentFilePath) && appState.RecentFiles.Contains(currentFilePath)) cbRecentFiles.SelectedItem = currentFilePath;
                else if (appState.RecentFiles.Count > 0) cbRecentFiles.SelectedIndex = 0;
            }
            catch { }
        }

        private void LstScripts_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (lstScripts!.SelectedIndex < 0) return;
            var selectedScript = luaScripts[lstScripts.SelectedIndex];
            txtCurrentScript!.Text = selectedScript.Script;
            UpdateTextScrollbars();
        }

        private void LstScripts_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var lb = sender as ListBox;
            if (lb == null) return;
            string text = lb.Items[e.Index]?.ToString() ?? string.Empty;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool focused = (e.State & DrawItemState.Focus) == DrawItemState.Focus;
            var dark = appState.IsDarkTheme;
            Color backColor;
            Color foreColor;
            if (dark)
            {
                backColor = selected ? accentColor : Color.FromArgb(30, 30, 30);
                foreColor = selected ? Color.White : Color.FromArgb(230, 230, 230);
            }
            else
            {
                backColor = selected ? accentColor : SystemColors.Window;
                foreColor = selected ? Color.White : Color.Black;
            }
            using (var bg = new SolidBrush(backColor)) e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, text, lb.Font, e.Bounds, foreColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (focused) e.DrawFocusRectangle();
            // keep custom scrollbar in sync
            UpdateListScrollbar();
        }

        

        private async void BtnLoadLuaFile_Click(object? sender, EventArgs e)
        {
            using var openFileDialog = new OpenFileDialog { Filter = "Lua files (*.lua)|*.lua|Text files (*.txt)|*.txt|All files (*.*)|*.*", Title = "Luaスクリプトファイルを選択" };
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    txtNewScript!.Text = await File.ReadAllTextAsync(openFileDialog.FileName);
                    txtNewScript.Modified = false;
                    SetupLuaFileWatcher(openFileDialog.FileName);
                }
                catch (Exception ex) { MessageBox.Show($"Luaファイルの読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                UpdateTextScrollbars();
            }
        }

        private void BtnReplace_Click(object? sender, EventArgs e)
        {
            if (lstScripts!.SelectedIndex < 0)
            {
                string msg = "置換するスクリプトを選択してください。";
                if (!suppressMessages && !isHttpRequest)
                {
                    MessageBox.Show(msg, "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                throw new Exception(msg);
            }
            if (string.IsNullOrWhiteSpace(txtNewScript!.Text))
            {
                string msg = "新しいスクリプトを入力してください。";
                if (!suppressMessages && !isHttpRequest)
                {
                    MessageBox.Show(msg, "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                throw new Exception(msg);
            }
            var selectedScript = luaScripts[lstScripts.SelectedIndex];
            selectedScript.Attribute.Value = txtNewScript.Text;
            selectedScript.Script = txtNewScript.Text;
            selectedScript.WasReplaced = true;
            txtCurrentScript!.Text = txtNewScript.Text;
            if (!suppressMessages && !isHttpRequest) MessageBox.Show("スクリプトを置換しました。保存するには「XMLを保存」ボタンをクリックしてください。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.F5)
            {
                suppressMessages = true;
                BtnReplace_Click(null, EventArgs.Empty);
                BtnSave_Click(null, EventArgs.Empty);
                suppressMessages = false;
                e.Handled = true;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Keys key = (Keys)vkCode;
                if ((Control.ModifierKeys & Keys.Control) != 0 && (Control.ModifierKeys & Keys.Shift) != 0 && key == Keys.F5)
                {
                    // Execute shortcut
                    suppressMessages = true;
                    BtnReplace_Click(null, EventArgs.Empty);
                    BtnSave_Click(null, EventArgs.Empty);
                    suppressMessages = false;
                    return (IntPtr)1; // Prevent further processing
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Stop and close HttpListener safely; it may already be stopped/disposed.
            try
            {
                if (httpListener != null)
                {
                    try { if (httpListener.IsListening) httpListener.Stop(); } catch { }
                    try { httpListener.Close(); } catch { }
                    httpListener = null;
                }
            }
            catch { }

            try { UnhookWindowsHookEx(_hookID); } catch { }
            base.OnFormClosing(e);
        }

        private async void BtnSave_Click(object? sender, EventArgs e)
        {
            if (vehicleXml == null || string.IsNullOrEmpty(currentFilePath)) { MessageBox.Show("XMLファイルが読み込まれていません。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try { await SaveXmlFileAsync(currentFilePath); if (!suppressMessages) MessageBox.Show("XMLファイルを保存しました。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception ex) { MessageBox.Show($"XMLファイルの保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private async Task LoadXmlFileAsync()
        {
            if (string.IsNullOrEmpty(currentFilePath) || !File.Exists(currentFilePath)) return;
            
            vehicleXml = await Task.Run(() =>
            {
                // XmlTextReaderを使用してNormalizationをfalseに設定しないと、
                // LoadOptions.PreserveWhitespaceを指定しても属性値の改行がスペースに置換されてしまう
                using (var reader = new XmlTextReader(currentFilePath))
                {
                    reader.Normalization = false;
                    return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                }
            });

            System.Console.WriteLine("Content:");
            System.Console.WriteLine(vehicleXml.ToString());
            ExtractLuaScripts();
            UpdateUI();
        }

        private async Task SaveXmlFileAsync(string path)
        {
            if (vehicleXml == null) return;
            // Find replaced scripts that are missing the prefix
            var prefix = appState.ScriptDetectionPrefix ?? string.Empty;
            var toUpdate = luaScripts.Where(s => s != null && s.WasReplaced)
                                     .Where(s => !(s.Script?.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false))
                                     .ToList();

            if (toUpdate.Count > 0 && !appState.SuppressPrefixPrompt && !isHttpRequest)
            {
                using (var dlg = new PrefixConfirmDialog(toUpdate.Select(s => s.DisplayName).ToList()))
                {
                    var dr = dlg.ShowDialog(this);
                    if (dr != DialogResult.OK) return; // user cancelled
                    if (dlg.DontShowAgain)
                    {
                        appState.SuppressPrefixPrompt = true;
                        SaveSettings();
                    }
                }
            }

            await Task.Run(() =>
            {
                try
                {
                    foreach (var s in toUpdate)
                    {
                        try { s.Script = prefix + "\n" + s.Script; } catch { }
                        try { s.Attribute.Value = s.Script; } catch { }
                    }
                }
                catch { }

                var settings = new System.Xml.XmlWriterSettings { Encoding = System.Text.Encoding.UTF8, Indent = true };
                using (var writer = System.Xml.XmlWriter.Create(path, settings)) vehicleXml!.Save(writer);
            }).ConfigureAwait(false);
        }

        // Translate known Japanese error messages into English for Stormworks HTTP responses
        private string TranslateErrorToEnglish(string? msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return "An unknown error occurred.";
            // Normalize
            var m = msg.Trim();
            if (m.Contains("XMLファイルが読み込まれていないか、スクリプトが選択されていません")) return "XML file not loaded or no script selected.";
            if (m.Contains("置換するスクリプトを選択してください")) return "Please select a script to replace.";
            if (m.Contains("新しいスクリプトを入力してください")) return "Please provide a new script.";
            if (m.Contains("XMLファイルが読み込まれていません")) return "XML file is not loaded.";
            if (m.Contains("Luaファイルの読み込みに失敗しました")) return m.Replace("Luaファイルの読み込みに失敗しました:", "Failed to load Lua file:");
            if (m.Contains("XMLファイルの読み込みに失敗しました")) return m.Replace("XMLファイルの読み込みに失敗しました:", "Failed to load XML file:");
            if (m.Contains("XMLファイルの保存に失敗しました")) return m.Replace("XMLファイルの保存に失敗しました:", "Failed to save XML file:");
            // If message appears to already be English, return as-is
            bool looksEnglish = System.Text.RegularExpressions.Regex.IsMatch(m, "[a-zA-Z]{2,}");
            if (looksEnglish) return m;
            // Fallback: return generic English message with original appended
            return "Error: " + m;
        }

        private void SetupFileWatcher()
        {
            try
            {
                fileWatcher.EnableRaisingEvents = false;
                if (string.IsNullOrWhiteSpace(currentFilePath)) return;

                var fullPath = Path.GetFullPath(currentFilePath);
                var directory = Path.GetDirectoryName(fullPath);
                var fileName = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory)) return;

                fileWatcher.Path = directory;
                fileWatcher.Filter = fileName;
                fileWatcher.EnableRaisingEvents = true;
                EnsureXmlReloadTimer();
            }
            catch { }
        }

        private void EnsureXmlReloadTimer()
        {
            if (reloadTimer != null) return;
            reloadTimer = new System.Windows.Forms.Timer { Interval = 500 };
            reloadTimer.Tick += async (s, e) =>
            {
                reloadTimer!.Stop();
                try
                {
                    int selectedIndex = lstScripts!.SelectedIndex;
                    await LoadXmlFileAsync();
                    if (selectedIndex >= 0 && selectedIndex < lstScripts.Items.Count) lstScripts.SelectedIndex = selectedIndex;
                    UpdateListScrollbar();
                    UpdateTextScrollbars();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"ファイルの再読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }

        private void SetupLuaFileWatcher(string? luaPath)
        {
            try
            {
                luaFileWatcher.EnableRaisingEvents = false;
                currentLuaFilePath = string.IsNullOrWhiteSpace(luaPath) ? null : Path.GetFullPath(luaPath);
                if (string.IsNullOrEmpty(currentLuaFilePath)) return;

                var directory = Path.GetDirectoryName(currentLuaFilePath);
                var fileName = Path.GetFileName(currentLuaFilePath);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
                {
                    currentLuaFilePath = null;
                    return;
                }

                luaFileWatcher.Path = directory;
                luaFileWatcher.Filter = fileName;
                luaFileWatcher.EnableRaisingEvents = true;
                EnsureLuaReloadTimer();
            }
            catch { currentLuaFilePath = null; }
        }

        private void EnsureLuaReloadTimer()
        {
            if (luaReloadTimer != null) return;
            luaReloadTimer = new System.Windows.Forms.Timer { Interval = 400 };
            luaReloadTimer.Tick += async (s, e) =>
            {
                luaReloadTimer!.Stop();
                if (string.IsNullOrEmpty(currentLuaFilePath) || txtNewScript == null) return;
                try
                {
                    var caret = txtNewScript.SelectionStart;
                    var text = await File.ReadAllTextAsync(currentLuaFilePath);
                    txtNewScript.Text = text;
                    txtNewScript.SelectionStart = Math.Min(caret, txtNewScript.TextLength);
                    txtNewScript.SelectionLength = 0;
                    txtNewScript.Modified = false;
                    UpdateTextScrollbars();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Luaファイルの再読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
        }

        // Settings persistence
        private string GetSettingsPath()
        {
            try
            {
                // Prefer exe folder so settings live next to the application by default
                string exeDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
                if (IsDirectoryWritable(exeDir))
                {
                    return Path.Combine(exeDir, "settings.json");
                }
            }
            catch { }
            // Fallback to per-user AppData location
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StormworksLuaReplacer");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        private bool IsDirectoryWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string testFile = Path.Combine(dir, ".__writetest.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch { return false; }
        }

        private void LoadSettings()
        {
            try
            {
                var path = GetSettingsPath();
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                var model = JsonSerializer.Deserialize<AppSettingsModel>(json);
                if (model == null) return;
                appState.ScriptDetectionPrefix = string.IsNullOrEmpty(model.ScriptDetectionPrefix) ? appState.ScriptDetectionPrefix : model.ScriptDetectionPrefix;
                appState.SuppressPrefixPrompt = model.SuppressPrefixPrompt;
                appState.IsDarkTheme = model.IsDarkTheme;
                appState.SettingsFilePath = string.IsNullOrWhiteSpace(model.SettingsFilePath) ? string.Empty : model.SettingsFilePath;
                if (model.RecentFiles != null) appState.RecentFiles = model.RecentFiles.ToList();
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var model = new AppSettingsModel
                {
                    ScriptDetectionPrefix = appState.ScriptDetectionPrefix,
                    SuppressPrefixPrompt = appState.SuppressPrefixPrompt,
                    IsDarkTheme = appState.IsDarkTheme,
                    SettingsFilePath = appState.SettingsFilePath ?? string.Empty,
                    RecentFiles = appState.RecentFiles ?? new List<string>()
                };
                var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
                // write to default path
                var defaultPath = GetSettingsPath();
                File.WriteAllText(defaultPath, json);
                // if user has chosen a custom path, write there as well
                try
                {
                    if (!string.IsNullOrWhiteSpace(model.SettingsFilePath))
                    {
                        var dir = Path.GetDirectoryName(model.SettingsFilePath) ?? Path.GetDirectoryName(defaultPath)!;
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(model.SettingsFilePath, json);
                    }
                }
                catch { }
            }
            catch { }
        }

        private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (!PathsEqual(e.FullPath, currentFilePath)) return;
            if (e.ChangeType == WatcherChangeTypes.Deleted)
            {
                vehicleXml = null;
                UpdateUI();
                return;
            }
            try { reloadTimer?.Stop(); reloadTimer?.Start(); } catch { }
        }

        private void LuaFileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (!PathsEqual(e.FullPath, currentLuaFilePath)) return;
            try { luaReloadTimer?.Stop(); luaReloadTimer?.Start(); } catch { }
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private void BtnSaveAs_Click(object? sender, EventArgs e)
        {
            if (vehicleXml == null) { MessageBox.Show("XMLファイルが読み込まれていません。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using var saveFileDialog = new SaveFileDialog { Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*", Title = "XMLファイルを保存", FileName = Path.GetFileName(currentFilePath) };
            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                var fileName = saveFileDialog.FileName;
                if (!string.IsNullOrEmpty(fileName))
                {
                    try { vehicleXml.Save(fileName); currentFilePath = fileName; UpdateUI(); MessageBox.Show("XMLファイルを保存しました。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch (Exception ex) { MessageBox.Show($"XMLファイルの保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    try { AddToRecentFiles(currentFilePath ?? string.Empty); } catch { }
                }
            }
        }

        private void BtnSettings_Click(object? sender, EventArgs e)
        {
            var currentSettingsPath = string.IsNullOrWhiteSpace(appState.SettingsFilePath) ? GetSettingsPath() : appState.SettingsFilePath;
            using var settingsDialog = new SettingsDialog(appState.ScriptDetectionPrefix, currentSettingsPath);
            if (settingsDialog.ShowDialog() == DialogResult.OK)
            {
                appState.ScriptDetectionPrefix = settingsDialog.DetectionPrefix;
                if (!string.IsNullOrWhiteSpace(settingsDialog.SettingsFilePath)) appState.SettingsFilePath = settingsDialog.SettingsFilePath;
                SaveSettings();
                if (vehicleXml != null) { ExtractLuaScripts(); UpdateUI(); MessageBox.Show($"検出条件を更新しました。\n{luaScripts.Count}個のスクリプトが見つかりました。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT)
                {
                    Point screenPoint = new Point(m.LParam.ToInt32());
                    Point clientPoint = this.PointToClient(screenPoint);
                    // If the point is over an interactive child control (e.g. titlebar buttons),
                    // don't treat it as a resize handle — allow the control to receive the click.
                    try
                    {
                        var topChild = this.GetChildAtPoint(clientPoint);
                        if (topChild != null)
                        {
                            var childLocal = topChild.PointToClient(screenPoint);
                            var inner = topChild.GetChildAtPoint(childLocal);
                            if (inner != null && (inner is Button || inner is ToolStrip || inner is Label))
                            {
                                return;
                            }
                        }
                    }
                    catch { }

                    bool left = clientPoint.X <= RESIZE_BORDER;
                    bool right = clientPoint.X >= this.ClientSize.Width - RESIZE_BORDER;
                    bool top = clientPoint.Y <= RESIZE_BORDER;
                    bool bottom = clientPoint.Y >= this.ClientSize.Height - RESIZE_BORDER;
                    if (left && top) m.Result = (IntPtr)HTTOPLEFT;
                    else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (left) m.Result = (IntPtr)HTLEFT;
                    else if (right) m.Result = (IntPtr)HTRIGHT;
                    else if (top) m.Result = (IntPtr)HTTOP;
                    else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { fileWatcher.Dispose(); } catch { }
            try { luaFileWatcher.Dispose(); } catch { }
            try { reloadTimer?.Stop(); luaReloadTimer?.Stop(); } catch { }
            try { reloadTimer?.Dispose(); luaReloadTimer?.Dispose(); } catch { }
        }

        [STAThread]
        static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm()); }
    }

    public class LuaScriptNode
    {
        public XElement Element { get; set; } = null!;
        public XAttribute Attribute { get; set; } = null!;
        public int Index { get; set; }
        public string Script { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        // Mark true when user replaces this script via the UI
        public bool WasReplaced { get; set; } = false;
    }

    public class CustomVScroll : Control
    {
        private int _minimum = 0;
        private int _maximum = 0;
        private int _value = 0;
        private bool dragging = false;
        private int dragOffset = 0;
        public int SmallChange { get; set; } = 1;
        public int LargeChange { get; set; } = 10;
        private int _thumbSizePixels = 0;
        /// <summary>
        /// Optional explicit thumb size in pixels. When > 0, this value is used
        /// to determine the slider height, allowing the caller to set thumb
        /// height based on visible line/item pixel height.
        /// </summary>
        public int ThumbSizePixels { get => _thumbSizePixels; set { _thumbSizePixels = Math.Max(0, value); Invalidate(); } }
        public int Minimum { get => _minimum; set { _minimum = value; Invalidate(); } }
        public int Maximum { get => _maximum; set { _maximum = Math.Max(0, value); Invalidate(); } }
        public int Value
        {
            get => _value;
            set
            {
                int v = Math.Max(Minimum, Math.Min(Maximum, value));
                if (v == _value) return;
                _value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? ValueChanged;

        public CustomVScroll()
        {
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            this.Width = 14;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(this.BackColor);
            int h = this.ClientSize.Height;
            int w = this.ClientSize.Width;
            int range = Math.Max(1, Maximum - Minimum + 1);
            int thumbHeight;
            if (ThumbSizePixels > 0)
            {
                thumbHeight = Math.Max(12, Math.Min(h - 2, ThumbSizePixels));
            }
            else
            {
                // Chrome-like proportional thumb: thumbHeight = (visible / totalContent) * trackHeight
                // totalContent approximated as (Maximum - Minimum + LargeChange)
                int totalContent = Math.Max(1, (Maximum - Minimum + LargeChange));
                double ratio = Math.Max(0.0, Math.Min(1.0, LargeChange / (double)totalContent));
                thumbHeight = Math.Max(12, (int)(h * ratio));
            }
            int track = h - thumbHeight;
            int thumbTop = track > 0 ? (int)(track * ((Value - Minimum) / (double)Math.Max(1, Maximum - Minimum))) : 0;
            var trackRect = new Rectangle(0, 0, w, h);
            using (var b = new SolidBrush(Color.Transparent)) { }
            Color trackColor = this.Parent != null && this.Parent.BackColor != Color.Empty ? ControlPaint.Dark(this.Parent.BackColor) : Color.FromArgb(60, 60, 60);
            using (var brush = new SolidBrush(trackColor)) g.FillRectangle(brush, trackRect);
            var thumbRect = new Rectangle(1, thumbTop, w - 2, thumbHeight);
            Color thumbColor = this.ForeColor;
            using (var b2 = new SolidBrush(thumbColor)) g.FillRectangle(b2, thumbRect);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int h = this.ClientSize.Height;
            int range = Math.Max(1, Maximum - Minimum + 1);
            int thumbHeight;
            if (ThumbSizePixels > 0)
            {
                thumbHeight = Math.Max(12, Math.Min(h - 2, ThumbSizePixels));
            }
            else
            {
                int totalContent = Math.Max(1, (Maximum - Minimum + LargeChange));
                double ratio = Math.Max(0.0, Math.Min(1.0, LargeChange / (double)totalContent));
                thumbHeight = Math.Max(12, (int)(h * ratio));
            }
            int track = h - thumbHeight;
            int thumbTop = track > 0 ? (int)(track * ((Value - Minimum) / (double)Math.Max(1, Maximum - Minimum))) : 0;
            var thumbRect = new Rectangle(1, thumbTop, this.ClientSize.Width - 2, thumbHeight);
            if (thumbRect.Contains(e.Location))
            {
                dragging = true; dragOffset = e.Y - thumbTop;
            }
            else
            {
                // click on track: page up/down
                if (e.Y < thumbTop) Value = Math.Max(Minimum, Value - LargeChange);
                else Value = Math.Min(Maximum, Value + LargeChange);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            int h = this.ClientSize.Height;
            int range = Math.Max(1, Maximum - Minimum + 1);
            int thumbHeight;
            if (ThumbSizePixels > 0)
            {
                thumbHeight = Math.Max(12, Math.Min(h - 2, ThumbSizePixels));
            }
            else
            {
                int totalContent = Math.Max(1, (Maximum - Minimum + LargeChange));
                double ratio = Math.Max(0.0, Math.Min(1.0, LargeChange / (double)totalContent));
                thumbHeight = Math.Max(12, (int)(h * ratio));
            }
            int track = h - thumbHeight;
            int y = e.Y - dragOffset;
            y = Math.Max(0, Math.Min(track, y));
            int newValue = Minimum + (int)( ( (double)y / Math.Max(1, track) ) * Math.Max(1, Maximum - Minimum) );
            Value = newValue;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }
    }

    // A lightweight modern dropdown control with a popup ListBox.
    public class ModernDropdown : Control
    {
        private readonly Label lblText;
        private readonly Button btnArrow;
        private List<string> _items = new List<string>();
        private int _selectedIndex = -1;
        private ToolStripDropDown? dropDown;
        private ToolStripControlHost? dropHost;
        private ListBox? popupListBox;
        private ToolStripDropDownClosedEventHandler? dropDownClosedHandler;
        private EventHandler? popupClickHandler;
        private EventHandler? popupDoubleClickHandler;
        private KeyEventHandler? popupKeyDownHandler;

        public event EventHandler? SelectedIndexChanged;

        public ModernDropdown()
        {
            this.Height = 26;
            this.BackColor = Color.White;
            this.ForeColor = Color.Black;
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

            lblText = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
            btnArrow = new Button { Dock = DockStyle.Right, Width = 28, FlatStyle = FlatStyle.Flat, Text = "▾" };
            btnArrow.FlatAppearance.BorderSize = 0;
            btnArrow.Click += (s, e) => TogglePopup();
            lblText.Click += (s, e) => TogglePopup();

            this.Controls.Add(lblText);
            this.Controls.Add(btnArrow);
        }

        public List<string> Items => _items;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (value < -1) value = -1;
                if (value >= _items.Count) value = _items.Count - 1;
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                lblText.Text = (_selectedIndex >= 0 && _selectedIndex < _items.Count) ? _items[_selectedIndex] : string.Empty;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string? SelectedItem
        {
            get => (_selectedIndex >= 0 && _selectedIndex < _items.Count) ? _items[_selectedIndex] : null;
            set
            {
                if (value == null) { SelectedIndex = -1; return; }
                int idx = _items.FindIndex(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));
                SelectedIndex = idx;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // draw border
            using (var pen = new Pen(ControlPaint.Dark(this.BackColor)))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
            }
        }

        private void TogglePopup()
        {
            if (dropDown != null && dropDown.Visible)
            {
                ClosePopup();
                return;
            }
            ShowPopup();
        }

        private void ShowPopup()
        {
            ClosePopup();
            popupListBox = new ListBox();
            popupListBox.BorderStyle = BorderStyle.None;
            popupListBox.SelectionMode = SelectionMode.One;
            popupClickHandler = (s, e) => { CommitSelectionFromPopup(); };
            popupDoubleClickHandler = (s, e) => { CommitSelectionFromPopup(); };
            popupKeyDownHandler = (s, e) => { if (e.KeyCode == Keys.Escape) { ClosePopup(); } if (e.KeyCode == Keys.Enter) { CommitSelectionFromPopup(); e.Handled = true; } };
            popupListBox.Click += popupClickHandler;
            popupListBox.DoubleClick += popupDoubleClickHandler;
            popupListBox.KeyDown += popupKeyDownHandler;

            foreach (var it in _items) popupListBox.Items.Add(it);
            popupListBox.BackColor = this.BackColor;
            popupListBox.ForeColor = this.ForeColor;

            // host the listbox inside a ToolStripDropDown so it doesn't steal focus
            dropHost = new ToolStripControlHost(popupListBox) { Padding = Padding.Empty };
            dropHost.AutoSize = false;
            int width = this.Width;
            int height = Math.Min(300, Math.Max(24, popupListBox.PreferredHeight));
            dropHost.Size = new Size(width, height);

            dropDown = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true };
            dropDown.Items.Add(dropHost);
            dropDownClosedHandler = new ToolStripDropDownClosedEventHandler((s, e) => { /* schedule cleanup to avoid re-entrancy */ this.BeginInvoke((Action)(() => ClosePopup())); });
            dropDown.Closed += dropDownClosedHandler;

            // Show relative to control; Show will not take focus away from main window
            dropDown.Show(this, new Point(0, this.Height));
        }

        private void CommitSelectionFromPopup()
        {
            if (popupListBox == null) return;
            int idx = popupListBox.SelectedIndex;
            if (idx >= 0 && idx < _items.Count)
            {
                SelectedIndex = idx;
            }
            if (dropDown != null) dropDown.Close();
            else ClosePopup();
        }

        private void ClosePopup()
        {
            try
            {
                if (dropDown != null)
                {
                    // detach the exact handler first to avoid Closed re-entry
                    if (dropDownClosedHandler != null) dropDown.Closed -= dropDownClosedHandler;
                    // safe to Close if still visible
                    if (dropDown.Visible) dropDown.Close();
                    dropDown.Dispose();
                }
            }
            catch { }
            try
            {
                if (popupListBox != null)
                {
                    if (popupClickHandler != null) popupListBox.Click -= popupClickHandler;
                    if (popupDoubleClickHandler != null) popupListBox.DoubleClick -= popupDoubleClickHandler;
                    if (popupKeyDownHandler != null) popupListBox.KeyDown -= popupKeyDownHandler;
                }
            }
            catch { }
            dropDown = null; dropHost = null; popupListBox = null;
            dropDownClosedHandler = null; popupClickHandler = null; popupDoubleClickHandler = null; popupKeyDownHandler = null;
        }

        public void UpdateItemsFromList(List<string> list)
        {
            _items = list ?? new List<string>();
            if (popupListBox != null)
            {
                popupListBox.Items.Clear();
                foreach (var it in _items) popupListBox.Items.Add(it);
            }
            // keep selection valid
            if (_selectedIndex >= _items.Count) _selectedIndex = _items.Count - 1;
            lblText.Text = (_selectedIndex >= 0 && _selectedIndex < _items.Count) ? _items[_selectedIndex] : string.Empty;
        }
    }

    public class SettingsDialog : Form
    {
        private readonly TextBox txtPrefix;
        private readonly TextBox txtSettingsPath;
        public string DetectionPrefix { get; private set; }
        public string SettingsFilePath { get => txtSettingsPath?.Text ?? string.Empty; }
        public SettingsDialog(string currentPrefix, string currentSettingsPath)
        {
            DetectionPrefix = currentPrefix;
            this.Text = "設定";
            this.Size = new System.Drawing.Size(640, 240);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var lblDescription = new Label { Text = "検出プレフィックスと設定保存先を指定してください。", Dock = DockStyle.Top, Height = 32, Padding = new Padding(8) };
            txtPrefix = new TextBox { Text = DetectionPrefix, Width = 420, Location = new System.Drawing.Point(150, 8), Font = new System.Drawing.Font("Consolas", 10) };
            var lblPrefix = new Label { Text = "検出プレフィックス:", AutoSize = true, Location = new System.Drawing.Point(8, 12) };

            var lblSettingsPath = new Label { Text = "設定ファイル保存先:", AutoSize = true, Location = new System.Drawing.Point(8, 48) };
            txtSettingsPath = new TextBox { Text = currentSettingsPath, Width = 480, Location = new System.Drawing.Point(150, 44) };
            var btnBrowse = new Button { Text = "参照...", Location = new System.Drawing.Point(540, 42), Width = 80 };
            btnBrowse.Click += (s, e) =>
            {
                using var sfd = new SaveFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", FileName = Path.GetFileName(currentSettingsPath), Title = "設定ファイルの保存先を選択" };
                if (sfd.ShowDialog(this) == DialogResult.OK) txtSettingsPath.Text = sfd.FileName;
            };

            var btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 100, Height = 34, Location = new System.Drawing.Point(450, 160) };
            btnOK.Click += (s, e) => { if (string.IsNullOrWhiteSpace(txtPrefix.Text)) { MessageBox.Show("プレフィックスを入力してください。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning); this.DialogResult = DialogResult.None; } else DetectionPrefix = txtPrefix.Text; };
            var btnCancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 100, Height = 34, Location = new System.Drawing.Point(540, 160) };

            // Add controls
            this.Controls.Add(lblDescription);
            this.Controls.Add(lblPrefix);
            this.Controls.Add(txtPrefix);
            this.Controls.Add(lblSettingsPath);
            this.Controls.Add(txtSettingsPath);
            this.Controls.Add(btnBrowse);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnOK; this.CancelButton = btnCancel;
        }
    }

    // Simple settings model persisted to %APPDATA%\StormworksLuaReplacer\settings.json
    public class AppSettingsModel
    {
        public string ScriptDetectionPrefix { get; set; } = "-- autochanger";
        public bool SuppressPrefixPrompt { get; set; } = false;
        public bool IsDarkTheme { get; set; } = true;
        public string SettingsFilePath { get; set; } = string.Empty;
        public List<string> RecentFiles { get; set; } = new List<string>();
    }
}