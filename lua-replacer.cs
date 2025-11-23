using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
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
        private const int RESIZE_BORDER = 8;
        private Point resizeStart;
        private Rectangle resizeStartBounds;
        private bool isResizing = false;

        public MainForm()
        {
            InitializeComponent();

            // ボーダーレスウィンドウでもリサイズ可能にする（フォーム全体のイベントを補助）
            this.MouseDown += MainForm_MouseDown;
            this.MouseMove += MainForm_MouseMove;
            this.MouseUp += MainForm_MouseUp;
            this.Cursor = Cursors.Default;

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

            // 画面上の座標へ変換してフォームのハンドラを呼ぶ
            var screenPt = ctrl.PointToScreen(e.Location);
            var formPt = this.PointToClient(screenPt);
            var fe = new MouseEventArgs(e.Button, e.Clicks, formPt.X, formPt.Y, e.Delta);
            // debug log removed
            MainForm_MouseDown(this, fe);
        }

        private void ChildControl_MouseMove(object? sender, MouseEventArgs e)
        {
            var ctrl = sender as Control;
            if (ctrl == null) return;
            var screenPt = ctrl.PointToScreen(e.Location);
            var formPt = this.PointToClient(screenPt);
            var fe = new MouseEventArgs(e.Button, e.Clicks, formPt.X, formPt.Y, e.Delta);
            // debug log removed
            MainForm_MouseMove(this, fe);
        }

        private void ChildControl_MouseUp(object? sender, MouseEventArgs e)
        {
            var ctrl = sender as Control;
            if (ctrl == null) return;
            var screenPt = ctrl.PointToScreen(e.Location);
            var formPt = this.PointToClient(screenPt);
            var fe = new MouseEventArgs(e.Button, e.Clicks, formPt.X, formPt.Y, e.Delta);
            // debug log removed
            MainForm_MouseUp(this, fe);
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
            btnMaximize.Click += (s, e) => {
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

        private void BtnLoadXml_Click(object? sender, EventArgs e)
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
                    LoadXmlFile();
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

        private void BtnLoadLuaFile_Click(object? sender, EventArgs e)
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
                    txtNewScript!.Text = File.ReadAllText(openFileDialog.FileName);
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

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            if (vehicleXml == null || string.IsNullOrEmpty(currentFilePath))
            {
                MessageBox.Show("XMLファイルが読み込まれていません。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                vehicleXml.Save(currentFilePath);
                MessageBox.Show("XMLファイルを保存しました。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"XMLファイルの保存に失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadXmlFile()
        {
            if (string.IsNullOrEmpty(currentFilePath)) return;
            vehicleXml = XDocument.Load(currentFilePath);
            ExtractLuaScripts();
            UpdateUI();
        }

        private void SetupFileWatcher()
        {
            if (string.IsNullOrEmpty(currentFilePath)) return;

            fileWatcher.EnableRaisingEvents = false;
            fileWatcher.Path = Path.GetDirectoryName(currentFilePath) ?? "";
            fileWatcher.Filter = Path.GetFileName(currentFilePath);
            fileWatcher.EnableRaisingEvents = true;
        }

        private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (appState.IsReloading) return;
            appState.IsReloading = true;
            
            this.Invoke((Action)(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(100);
                    
                    int selectedIndex = lstScripts!.SelectedIndex;
                    LoadXmlFile();

                    if (selectedIndex >= 0 && selectedIndex < lstScripts.Items.Count)
                    {
                        lstScripts.SelectedIndex = selectedIndex;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"ファイルの再読み込みに失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    appState.IsReloading = false;
                }
            }));
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

        /// <summary>
        /// ウィンドウのリサイズを処理するマウスダウンイベント
        /// </summary>
        private void MainForm_MouseDown(object? sender, MouseEventArgs e)
        {
            // debug log removed

            // カーソル判定を先に行い、リサイズ開始フラグを設定
            UpdateResizeCursor(e.Location);

            resizeStart = e.Location;
            resizeStartBounds = this.Bounds;

            // カーソルがリサイズ用になっていればリサイズモードに入る
            isResizing = this.Cursor != Cursors.Default;
            // debug log removed
        }

        /// <summary>
        /// マウス位置に応じてカーソルを変更し、リサイズ処理を実行
        /// </summary>
        private void MainForm_MouseMove(object? sender, MouseEventArgs e)
        {
            if (e == null) return;
            // フォーム最小化状態では処理しない
            if (this.WindowState == FormWindowState.Minimized)
                return;

            // リサイズ処理中かどうかを判定（フラグを使う）
            if (isResizing && e.Button == MouseButtons.Left)
            {
                // debug log removed
                ResizeWindow(e.Location);
            }
            else
            {
                // カーソルをリサイズ対象位置に応じて更新
                UpdateResizeCursor(e.Location);
            }
        }

        /// <summary>
        /// マウスアップでリサイズ開始位置をリセット
        /// </summary>
        private void MainForm_MouseUp(object? sender, MouseEventArgs e)
        {
            // debug log removed
            isResizing = false;
            resizeStart = Point.Empty;
        }

        /// <summary>
        /// マウス位置に応じてリサイズカーソルを設定
        /// </summary>
        private void UpdateResizeCursor(Point location)
        {
            bool isLeft = location.X < RESIZE_BORDER;
            bool isRight = location.X > this.Width - RESIZE_BORDER;
            bool isTop = location.Y < RESIZE_BORDER;
            bool isBottom = location.Y > this.Height - RESIZE_BORDER;

            Cursor newCursor;
            if ((isLeft && isTop) || (isRight && isBottom))
                newCursor = Cursors.SizeNWSE;
            else if ((isRight && isTop) || (isLeft && isBottom))
                newCursor = Cursors.SizeNESW;
            else if (isLeft || isRight)
                newCursor = Cursors.SizeWE;
            else if (isTop || isBottom)
                newCursor = Cursors.SizeNS;
            else
                newCursor = Cursors.Default;

            if (this.Cursor != newCursor)
            {
                // debug log removed
                this.Cursor = newCursor;
            }
        }

        /// <summary>
        /// マウス位置に基づいてウィンドウをリサイズ
        /// </summary>
        private void ResizeWindow(Point currentLocation)
        {
            int deltaX = currentLocation.X - resizeStart.X;
            int deltaY = currentLocation.Y - resizeStart.Y;

            int newLeft = resizeStartBounds.Left;
            int newTop = resizeStartBounds.Top;
            int newWidth = resizeStartBounds.Width;
            int newHeight = resizeStartBounds.Height;

            bool isLeft = resizeStart.X < RESIZE_BORDER;
            bool isRight = resizeStart.X > resizeStartBounds.Width - RESIZE_BORDER;
            bool isTop = resizeStart.Y < RESIZE_BORDER;
            bool isBottom = resizeStart.Y > resizeStartBounds.Height - RESIZE_BORDER;

            // 左辺のリサイズ
            if (isLeft)
            {
                newLeft += deltaX;
                newWidth -= deltaX;
            }
            // 右辺のリサイズ
            else if (isRight)
            {
                newWidth += deltaX;
            }

            // 上辺のリサイズ
            if (isTop)
            {
                newTop += deltaY;
                newHeight -= deltaY;
            }
            // 下辺のリサイズ
            else if (isBottom)
            {
                newHeight += deltaY;
            }

            // 最小サイズを保証
            if (newWidth < 400) newWidth = 400;
            if (newHeight < 300) newHeight = 300;

            // debug log removed
            this.Bounds = new Rectangle(newLeft, newTop, newWidth, newHeight);
        }

        // WM_NCHITTEST をオーバーライドしてネイティブのリサイズを有効にする。デバッグ出力も追加。
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            if (m.Msg == WM_NCHITTEST && this.FormBorderStyle == FormBorderStyle.None)
            {
                // まずデフォルト処理してからカスタム判定を上書きする
                base.WndProc(ref m);

                try
                {
                    if ((int)m.Result == HTCLIENT)
                    {
                        int lParam = m.LParam.ToInt32();
                        int x = (short)(lParam & 0xFFFF);
                        int y = (short)((lParam >> 16) & 0xFFFF);
                        Point clientPoint = this.PointToClient(new Point(x, y));

                        bool left = clientPoint.X <= RESIZE_BORDER;
                        bool right = clientPoint.X >= this.ClientSize.Width - RESIZE_BORDER;
                        bool top = clientPoint.Y <= RESIZE_BORDER;
                        bool bottom = clientPoint.Y >= this.ClientSize.Height - RESIZE_BORDER;

                        if (left && top) m.Result = (IntPtr)HTTOPLEFT;
                        else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
                        else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
                        else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
                        else if (left) m.Result = (IntPtr)HTLEFT;
                        else if (right) m.Result = (IntPtr)HTRIGHT;
                        else if (top) m.Result = (IntPtr)HTTOP;
                        else if (bottom) m.Result = (IntPtr)HTBOTTOM;

                        // debug log removed
                    }
                }
                catch (Exception)
                {
                    // debug log removed
                }
                return;
            }

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