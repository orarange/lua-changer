using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StormworksLuaReplacer
{
    /// <summary>
    /// アプリケーション状態を管理するクラス
    /// </summary>
    public class ApplicationState
    {
        /// <summary>ファイル再読み込み中フラグ</summary>
        public bool IsReloading { get; set; }

        /// <summary>スクリプト検出プレフィックス（デフォルト: "-- autochanger"）</summary>
        public string ScriptDetectionPrefix { get; set; } = "-- autochanger";

        /// <summary>カスタムタイトルバー用マウス位置</summary>
        public Point MouseLocation { get; set; }
    }

    public partial class MainForm : Form
    {
        private Point resizeStart;
        private Rectangle resizeStartBounds;
        private bool isResizing = false;
        private int resizeMode = 0;
        private XDocument? vehicleXml;
        private string? currentFilePath;
        private readonly List<LuaScriptNode> luaScripts = new List<LuaScriptNode>();
        private readonly FileSystemWatcher fileWatcher;
        private readonly ApplicationState appState = new ApplicationState();

        // UI Controls
        private Label? lblFilePath;
        private ListBox? lstScripts;
        private TextBox? txtCurrentScript;
        private TextBox? txtNewScript;

        // リサイズ関連
        // Windows APIの定数定義
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2; // タイトルバー（ドラッグ移動用）
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        // リサイズ判定を行う境界線の太さ
        private const int RESIZE_BORDER = 8;

        // ファイル監視の再読み込みデバウンス
        private System.Windows.Forms.Timer? reloadTimer;

        public MainForm()
        {
            InitializeComponent();
            this.MinimumSize = new Size(400, 300);


            // 子コントロールがマウスイベントを奪ってしまうことが多いので、全子コントロールにも
            // フォワード用ハンドラを登録してフォームのハンドラに渡す
            AttachMouseHandlers(this);

            fileWatcher = new FileSystemWatcher { NotifyFilter = NotifyFilters.LastWrite };
            fileWatcher.Changed += FileWatcher_Changed;
        }

        // Log method removed (debug code cleaned)

        /// <summary>
        /// 再帰的に子コントロールへマウスイベントを登録してフォームにフォワードする。
        /// （ボーダーレスで子コントロールがフォームの MouseDown/Move を奪うケース対策）
        /// </summary>
        private void AttachMouseHandlers(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                // フォワード用に登録（必要なイベントをカバー）
                c.MouseDown += ChildControl_MouseDown;
                c.MouseMove += ChildControl_MouseMove;
                c.MouseUp += ChildControl_MouseUp;

                // 一部コントロールは内部でマウスをキャプチャするので、MouseEnter等もログしておくと良い
                c.MouseEnter += (s, e) => { };
                c.MouseLeave += (s, e) => { };

                if (c.HasChildren) AttachMouseHandlers(c);
            }
        }

        // ヘルパー: フォームのクライアント座標がリサイズ領域にあるかを判定
        private bool IsPointInResizeZone(Point clientPoint)
        {
            bool left = clientPoint.X <= RESIZE_BORDER;
            bool right = clientPoint.X >= this.ClientSize.Width - RESIZE_BORDER;
            bool top = clientPoint.Y <= RESIZE_BORDER;
            bool bottom = clientPoint.Y >= this.ClientSize.Height - RESIZE_BORDER;
            return left || right || top || bottom;
        }

        private string GetControlPath(Control c)
        {
            var parts = new List<string>();
            var cur = c;
            while (cur != null)
            {
                parts.Add(cur.GetType().Name + (string.IsNullOrEmpty(cur.Name) ? "" : $"({cur.Name})"));
                cur = cur.Parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private void ChildControl_MouseDown(object? sender, MouseEventArgs e)
        {
            var ctrl = sender as Control;
            if (ctrl == null) return;

            var screenPt = ctrl.PointToScreen(e.Location);
            var formPt = this.PointToClient(screenPt); // フォーム基準の座標

            // リサイズ領域にいるかチェック
            int mode = GetResizeMode(formPt);
            if (mode != HTCLIENT)
            {
                // リサイズ開始
                isResizing = true;
                resizeMode = mode;
                resizeStart = screenPt;          // 開始時のスクリーン座標
                resizeStartBounds = this.Bounds; // 開始時のウィンドウ位置・サイズ

                // マウスキャプチャ（ドラッグ中にマウスが外れても追跡するため）
                ctrl.Capture = true;
            }
        }

        private void ChildControl_MouseMove(object? sender, MouseEventArgs e)
        {
            var ctrl = sender as Control;
            if (ctrl == null) return;

            var screenPt = ctrl.PointToScreen(e.Location);

            // リサイズ実行中
            if (isResizing)
            {
                ResizeWindow(screenPt);
                return;
            }

            // リサイズ中でない場合、カーソルの見た目を更新
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

                // キャプチャ解除
                var ctrl = sender as Control;
                if (ctrl != null) ctrl.Capture = false;
            }
        }

        private void ResizeWindow(Point currentScreenLocation)
        {
            if (!isResizing) return;

            // マウスの移動量を計算
            int deltaX = currentScreenLocation.X - resizeStart.X;
            int deltaY = currentScreenLocation.Y - resizeStart.Y;

            // 最小サイズの定義
            const int MIN_WIDTH = 400;
            const int MIN_HEIGHT = 300;

            int newLeft = resizeStartBounds.Left;
            int newTop = resizeStartBounds.Top;
            int newWidth = resizeStartBounds.Width;
            int newHeight = resizeStartBounds.Height;

            // resizeMode は WndProc の定数(HTLEFT等)を借用して方向を判定します
            // HTLEFT=10, HTRIGHT=11, HTTOP=12, HTTOPLEFT=13, etc...

            bool isLeft = (resizeMode == HTLEFT || resizeMode == HTTOPLEFT || resizeMode == HTBOTTOMLEFT);
            bool isRight = (resizeMode == HTRIGHT || resizeMode == HTTOPRIGHT || resizeMode == HTBOTTOMRIGHT);
            bool isTop = (resizeMode == HTTOP || resizeMode == HTTOPLEFT || resizeMode == HTTOPRIGHT);
            bool isBottom = (resizeMode == HTBOTTOM || resizeMode == HTBOTTOMLEFT || resizeMode == HTBOTTOMRIGHT);

            // --- 横方向の計算 ---
            if (isLeft)
            {
                // 1. まず「仮の幅」を計算
                int proposedWidth = resizeStartBounds.Width - deltaX;

                // 2. 最小幅で制限
                if (proposedWidth < MIN_WIDTH) proposedWidth = MIN_WIDTH;

                // 3. 【重要】右端（Right）を固定点として、新しい幅から Left を逆算する
                //    NewLeft = (元のRight) - NewWidth
                newWidth = proposedWidth;
                newLeft = (resizeStartBounds.Left + resizeStartBounds.Width) - newWidth;
            }
            else if (isRight)
            {
                newWidth = resizeStartBounds.Width + deltaX;
                if (newWidth < MIN_WIDTH) newWidth = MIN_WIDTH;
            }

            // --- 縦方向の計算 ---
            if (isTop)
            {
                // 1. まず「仮の高さ」を計算
                int proposedHeight = resizeStartBounds.Height - deltaY;

                // 2. 最小高さで制限
                if (proposedHeight < MIN_HEIGHT) proposedHeight = MIN_HEIGHT;

                // 3. 【重要】下端（Bottom）を固定点として、新しい高さから Top を逆算する
                newHeight = proposedHeight;
                newTop = (resizeStartBounds.Top + resizeStartBounds.Height) - newHeight;
            }
            else if (isBottom)
            {
                newHeight = resizeStartBounds.Height + deltaY;
                if (newHeight < MIN_HEIGHT) newHeight = MIN_HEIGHT;
            }

            // 境界を設定
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
            return HTCLIENT; // リサイズ領域ではない
        }

        private void UpdateCursor(int mode)
        {
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
            // Initialize UI Controls first
            lblFilePath = new Label { Text = "ファイル: 未選択", Dock = DockStyle.Fill, AutoSize = false, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            lstScripts = new ListBox { Dock = DockStyle.Fill, Height = 300 };
            txtCurrentScript = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new System.Drawing.Font("Consolas", 10), ReadOnly = true };
            txtNewScript = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new System.Drawing.Font("Consolas", 10) };

            this.FormBorderStyle = FormBorderStyle.None; // Remove default title bar
            this.Text = ""; // Empty text for custom title bar

            // Custom Title Bar Panel
            var pnlTitleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = System.Drawing.Color.FromArgb(45, 45, 48) // Dark gray
            };

            var lblTitle = new Label
            {
                Text = "Stormworks Lua Script Replacer",
                ForeColor = System.Drawing.Color.White,
                Location = new System.Drawing.Point(10, 8)
            };

            var btnMaximize = new Button
            {
                Text = "🗖", // Maximize symbol
                Dock = DockStyle.Right,
                Width = 45,
                FlatStyle = FlatStyle.Flat,
                ForeColor = System.Drawing.Color.White,
                BackColor = System.Drawing.Color.FromArgb(45, 45, 48)
            };
            btnMaximize.FlatAppearance.BorderSize = 0;
            btnMaximize.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(63, 63, 70);

            var btnMinimize = new Button
            {
                Text = "—", // Minimize symbol
                Dock = DockStyle.Right,
                Width = 45,
                FlatStyle = FlatStyle.Flat,
                ForeColor = System.Drawing.Color.White,
                BackColor = System.Drawing.Color.FromArgb(45, 45, 48)
            };

            var btnClose = new Button
            {
                Text = "✕", // Close symbol
                Dock = DockStyle.Right,
                Width = 45,
                FlatStyle = FlatStyle.Flat,
                ForeColor = System.Drawing.Color.White,
                BackColor = System.Drawing.Color.FromArgb(45, 45, 48)
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(212, 63, 63); // Red on hover

            btnMinimize.FlatAppearance.BorderSize = 0;
            btnMinimize.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(63, 63, 70);

            pnlTitleBar.Controls.Add(lblTitle);
            pnlTitleBar.Controls.Add(btnMinimize);
            pnlTitleBar.Controls.Add(btnMaximize);
            pnlTitleBar.Controls.Add(btnClose);

            // Event Handlers for custom title bar
            btnClose.Click += (s, e) => this.Close();
            btnMinimize.Click += (s, e) => this.WindowState = FormWindowState.Minimized;
            btnMaximize.Click += (s, e) =>
            {
                this.WindowState = this.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                btnMaximize.Text = this.WindowState == FormWindowState.Maximized ? "🗗" : "🗖"; // Restore/Maximize symbol
            };

            // Drag functionality (改良版)
            pnlTitleBar.MouseDown += (s, e) =>
            {
                // クリック位置をフォームのクライアント座標に変換してリサイズ領域を判定
                var screenPt = pnlTitleBar.PointToScreen(e.Location);
                var formPt = this.PointToClient(screenPt);

                // リサイズエッジ内ならドラッグ処理は開始しない（ネイティブのリサイズ/当方のリサイズ処理に委ねる）
                if (IsPointInResizeZone(formPt))
                    return;

                appState.MouseLocation = e.Location;
            };

            pnlTitleBar.MouseMove += (s, e) =>
            {
                // マウス左押下で移動を行うが、移動中にリサイズエッジに進入したら移動を停止する
                if (e.Button == MouseButtons.Left && appState.MouseLocation != Point.Empty)
                {
                    var screenPt = pnlTitleBar.PointToScreen(e.Location);
                    var formPt = this.PointToClient(screenPt);

                    if (IsPointInResizeZone(formPt))
                    {
                        // 移動をブロックして、以降はリサイズ側の処理に任せる
                        return;
                    }

                    this.Left += e.X - appState.MouseLocation.X;
                    this.Top += e.Y - appState.MouseLocation.Y;
                }
            };

            // lblTitle でネイティブドラッグを発火させる既存コードもリサイズ領域を考慮する
            lblTitle.MouseDown += (s, e) =>
            {
                // lblTitle の座標系 -> 画面 -> フォーム座標に変換
                var screenPt = lblTitle.PointToScreen(e.Location);
                var formPt = this.PointToClient(screenPt);

                if (IsPointInResizeZone(formPt))
                    return;

                // 既存のネイティブドラッグ（HTCAPTION 相当）を発生させる
                pnlTitleBar.Capture = false;
                Message msg = Message.Create(pnlTitleBar.Handle, 0x00A1, (IntPtr)0x0002, IntPtr.Zero);
                this.DefWndProc(ref msg);
            };

            // MenuStrip
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
            toolsMenu.DropDownItems.Add(settingsItem);

            // Remove image margin from menu items
            ((ToolStripDropDownMenu)fileMenu.DropDown).ShowImageMargin = false;
            ((ToolStripDropDownMenu)editMenu.DropDown).ShowImageMargin = false;
            ((ToolStripDropDownMenu)toolsMenu.DropDown).ShowImageMargin = false;

            menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, toolsMenu });

            // ToolStrip
            var toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            var openXmlBtn = new ToolStripButton("XMLを開く", null, BtnLoadXml_Click) { Margin = new Padding(5, 1, 0, 2) };
            var saveBtn = new ToolStripButton("保存", null, BtnSave_Click);
            var loadLuaBtn = new ToolStripButton("Lua読込", null, BtnLoadLuaFile_Click);
            var replaceBtn = new ToolStripButton("置換", null, BtnReplace_Click);
            var settingsBtn = new ToolStripButton("設定", null, BtnSettings_Click);

            toolStrip.Items.AddRange(new ToolStripItem[] { openXmlBtn, saveBtn, new ToolStripSeparator(), loadLuaBtn, replaceBtn, new ToolStripSeparator(), settingsBtn });


            this.Text = "Stormworks Lua Script Replacer";
            this.Size = new System.Drawing.Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;

            // Set up event handlers
            lstScripts!.SelectedIndexChanged += LstScripts_SelectedIndexChanged;

            // Create script content panels
            var grpCurrentScript = new GroupBox { Text = "現在のスクリプト", Dock = DockStyle.Fill, Controls = { txtCurrentScript } };
            var grpNewScript = new GroupBox { Text = "新しいスクリプト", Dock = DockStyle.Fill, Controls = { txtNewScript } };

            // Create main layout
            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Add file path label
            mainLayout.Controls.Add(lblFilePath, 0, 0);
            mainLayout.SetColumnSpan(lblFilePath, 2);

            // Add script list panel
            var scriptListPanel = new Panel { Dock = DockStyle.Fill, Controls = { lstScripts } };
            mainLayout.Controls.Add(scriptListPanel, 0, 0);
            mainLayout.SetColumnSpan(scriptListPanel, 2);

            // Add script content panels
            mainLayout.Controls.Add(grpCurrentScript, 0, 1);
            mainLayout.Controls.Add(grpNewScript, 1, 1);

            // Add controls to form in correct order (top to bottom)
            this.Controls.Add(mainLayout);
            this.Controls.Add(toolStrip);
            this.Controls.Add(menuStrip);
            this.Controls.Add(pnlTitleBar);
            this.MainMenuStrip = menuStrip;
        }

        private async void BtnLoadXml_Click(object? sender, EventArgs e)
        {
            using var openFileDialog = new OpenFileDialog
            {
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "ビークルXMLファイルを選択"
            };

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    currentFilePath = openFileDialog.FileName;
                    await LoadXmlFileAsync();
                    SetupFileWatcher();
                    MessageBox.Show($"XMLファイルを読み込みました。\n{luaScripts.Count}個のLuaスクリプトが見つかりました。",
                        "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"XMLファイルの読み込みに失敗しました:\n{ex.Message}",
                        "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExtractLuaScripts()
        {
            luaScripts.Clear();
            if (vehicleXml == null) return;

            var scriptElements = vehicleXml.Descendants()
                .Where(e => e.Attribute("script")?.Value.Trim().StartsWith(appState.ScriptDetectionPrefix, StringComparison.OrdinalIgnoreCase) ?? false);

            luaScripts.AddRange(scriptElements.Select((element, index) =>
            {
                var scriptAttribute = element.Attribute("script")!;
                var scriptContent = scriptAttribute.Value;
                var lines = scriptContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                string identifier = lines.Length > 0 ? lines[0].Substring(2).Trim() : "Unknown Script";
                if (lines.Length > 1 && lines[1].Trim().StartsWith("--"))
                {
                    identifier += " " + lines[1].Substring(2).Trim();
                }

                return new LuaScriptNode
                {
                    Element = element,
                    Attribute = scriptAttribute,
                    Index = index + 1,
                    Script = scriptContent,
                    DisplayName = identifier
                };
            }));
        }

        private void UpdateUI()
        {
            lblFilePath!.Text = $"ファイル: {currentFilePath}";

            lstScripts!.Items.Clear();
            foreach (var script in luaScripts)
            {
                lstScripts.Items.Add(script.DisplayName);
            }
        }

        private void LstScripts_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (lstScripts!.SelectedIndex < 0) return;

            var selectedScript = luaScripts[lstScripts.SelectedIndex];
            txtCurrentScript!.Text = selectedScript.Script;

            if (string.IsNullOrEmpty(txtNewScript!.Text))
            {
                txtNewScript.Text = selectedScript.Script;
            }
        }

        private async void BtnLoadLuaFile_Click(object? sender, EventArgs e)
        {
            using var openFileDialog = new OpenFileDialog
            {
                Filter = "Lua files (*.lua)|*.lua|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Luaスクリプトファイルを選択"
            };

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    txtNewScript!.Text = await File.ReadAllTextAsync(openFileDialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Luaファイルの読み込みに失敗しました:\n{ex.Message}",
                        "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnReplace_Click(object? sender, EventArgs e)
        {
            if (lstScripts!.SelectedIndex < 0)
            {
                MessageBox.Show("置換するスクリプトを選択してください。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtNewScript!.Text))
            {
                MessageBox.Show("新しいスクリプトを入力してください。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selectedScript = luaScripts[lstScripts.SelectedIndex];
            selectedScript.Attribute.Value = txtNewScript.Text;
            selectedScript.Script = txtNewScript.Text;
            txtCurrentScript!.Text = txtNewScript.Text;

            MessageBox.Show("スクリプトを置換しました。保存するには「XMLを保存」ボタンをクリックしてください。",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async void BtnSave_Click(object? sender, EventArgs e)
        {
            if (vehicleXml == null || string.IsNullOrEmpty(currentFilePath))
            {
                MessageBox.Show("XMLファイルが読み込まれていません。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                await SaveXmlFileAsync(currentFilePath);
                MessageBox.Show("XMLファイルを保存しました。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"XMLファイルの保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task LoadXmlFileAsync()
        {
            if (string.IsNullOrEmpty(currentFilePath)) return;
            // Load on background thread to avoid blocking UI
            vehicleXml = await Task.Run(() => XDocument.Load(currentFilePath));
            ExtractLuaScripts();
            UpdateUI();
        }

        private Task SaveXmlFileAsync(string path)
        {
            return Task.Run(() =>
            {
                var settings = new System.Xml.XmlWriterSettings
                {
                    Encoding = System.Text.Encoding.UTF8,
                    Indent = true
                };
                using (var writer = System.Xml.XmlWriter.Create(path, settings))
                {
                    vehicleXml!.Save(writer);
                }
            });
        }

        private void SetupFileWatcher()
        {
            if (string.IsNullOrEmpty(currentFilePath)) return;

            fileWatcher.EnableRaisingEvents = false;
            fileWatcher.Path = Path.GetDirectoryName(currentFilePath) ?? "";
            fileWatcher.Filter = Path.GetFileName(currentFilePath);
            fileWatcher.EnableRaisingEvents = true;

            // initialize debounce timer
            if (reloadTimer == null)
            {
                reloadTimer = new System.Windows.Forms.Timer();
                reloadTimer.Interval = 500; // ms
                reloadTimer.Tick += async (s, e) =>
                {
                    reloadTimer!.Stop();
                    try
                    {
                        int selectedIndex = lstScripts!.SelectedIndex;
                        await LoadXmlFileAsync();
                        if (selectedIndex >= 0 && selectedIndex < lstScripts.Items.Count)
                        {
                            lstScripts.SelectedIndex = selectedIndex;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"ファイルの再読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
            }
        }

        private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            // Debounce rapid events by restarting the timer
            try
            {
                reloadTimer?.Stop();
                reloadTimer?.Start();
            }
            catch { }
        }

        private void BtnSaveAs_Click(object? sender, EventArgs e)
        {
            if (vehicleXml == null)
            {
                MessageBox.Show("XMLファイルが読み込まれていません。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var saveFileDialog = new SaveFileDialog
            {
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "XMLファイルを保存",
                FileName = Path.GetFileName(currentFilePath)
            };

            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                var fileName = saveFileDialog.FileName;
                if (!string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        vehicleXml.Save(fileName);
                        currentFilePath = fileName;
                        UpdateUI();
                        MessageBox.Show("XMLファイルを保存しました。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"XMLファイルの保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnSettings_Click(object? sender, EventArgs e)
        {
            using var settingsDialog = new SettingsDialog(appState.ScriptDetectionPrefix);
            if (settingsDialog.ShowDialog() == DialogResult.OK)
            {
                appState.ScriptDetectionPrefix = settingsDialog.DetectionPrefix;

                if (vehicleXml != null)
                {
                    ExtractLuaScripts();
                    UpdateUI();
                    MessageBox.Show($"検出条件を更新しました。\n{luaScripts.Count}個のスクリプトが見つかりました。",
                        "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }


        protected override void WndProc(ref Message m)
        {
            // メッセージが「マウスの当たり判定チェック (WM_NCHITTEST)」の場合のみ割り込む
            if (m.Msg == WM_NCHITTEST)
            {
                // 1. まずは通常の処理をさせる（クライアント領域かどうかの判定など）
                base.WndProc(ref m);

                // 2. 結果が「クライアント領域 (HTCLIENT)」なら、端っこかどうか詳しくチェックする
                if ((int)m.Result == HTCLIENT)
                {
                    // マウス座標（スクリーン座標）を取得
                    Point screenPoint = new Point(m.LParam.ToInt32());
                    // スクリーン座標をフォーム内のクライアント座標に変換
                    Point clientPoint = this.PointToClient(screenPoint);

                    // 判定ロジック
                    bool left = clientPoint.X <= RESIZE_BORDER;
                    bool right = clientPoint.X >= this.ClientSize.Width - RESIZE_BORDER;
                    bool top = clientPoint.Y <= RESIZE_BORDER;
                    bool bottom = clientPoint.Y >= this.ClientSize.Height - RESIZE_BORDER;

                    if (left && top)
                        m.Result = (IntPtr)HTTOPLEFT;
                    else if (right && top)
                        m.Result = (IntPtr)HTTOPRIGHT;
                    else if (left && bottom)
                        m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (right && bottom)
                        m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (left)
                        m.Result = (IntPtr)HTLEFT;
                    else if (right)
                        m.Result = (IntPtr)HTRIGHT;
                    else if (top)
                        m.Result = (IntPtr)HTTOP;
                    else if (bottom)
                        m.Result = (IntPtr)HTBOTTOM;

                    // 必要であれば、上部の特定のエリアを「タイトルバー(HTCAPTION)」と判定して
                    // OS標準のウィンドウ移動機能を使うことも可能ですが、
                    // 既存の pnlTitleBar のドラッグ処理がある場合はそのままでOKです。
                }
                return;
            }

            // その他のメッセージは通常通り処理
            base.WndProc(ref m);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // debug resources cleaned up (no-op)
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class LuaScriptNode
    {
        public XElement Element { get; set; } = null!;
        public XAttribute Attribute { get; set; } = null!;
        public int Index { get; set; }
        public string Script { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    public class SettingsDialog : Form
    {
        private readonly TextBox txtPrefix;
        public string DetectionPrefix { get; private set; }

        public SettingsDialog(string currentPrefix)
        {
            DetectionPrefix = currentPrefix;

            this.Text = "スクリプト検出設定";
            this.Size = new System.Drawing.Size(500, 200);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var lblDescription = new Label
            {
                Text = "検出するスクリプトの先頭コメントプレフィックスを設定してください。\n例: \"-- autochanger\" と入力すると、この文字列で始まるス...",
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(0, 0, 0, 15)
            };

            txtPrefix = new TextBox
            {
                Text = DetectionPrefix,
                Width = 300,
                Location = new System.Drawing.Point(130, 5),
                Font = new System.Drawing.Font("Consolas", 10)
            };

            var pnlInput = new Panel { Dock = DockStyle.Fill, Height = 35 };
            pnlInput.Controls.Add(new Label { Text = "検出プレフィックス:", AutoSize = true, Location = new System.Drawing.Point(0, 8) });
            pnlInput.Controls.Add(txtPrefix);

            var btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80, Height = 30 };
            btnOK.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtPrefix.Text))
                {
                    MessageBox.Show("プレフィックスを入力してください。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None; // Keep dialog open
                }
                else
                {
                    DetectionPrefix = txtPrefix.Text;
                }
            };

            var btnCancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 80, Height = 30 };

            var pnlButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            pnlButtons.Controls.Add(btnCancel);
            pnlButtons.Controls.Add(btnOK);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(15)
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));

            mainLayout.Controls.Add(lblDescription, 0, 0);
            mainLayout.Controls.Add(pnlInput, 0, 1);
            mainLayout.Controls.Add(pnlButtons, 0, 2);

            this.Controls.Add(mainLayout);
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }
    }
}