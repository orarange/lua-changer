using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace StormworksLuaReplacer
{
    public partial class MainForm : Form
    {
        private XDocument? vehicleXml;
        private string? currentFilePath;
        private readonly List<LuaScriptNode> luaScripts = new List<LuaScriptNode>();
        private readonly FileSystemWatcher fileWatcher;
        private bool isReloading = false;
        private string scriptDetectionPrefix = "-- autochanger";

        // UI Controls
        private readonly Label lblFilePath;
        private readonly ListBox lstScripts;
        private readonly TextBox txtCurrentScript;
        private readonly TextBox txtNewScript;

        // For custom title bar
        private Point mouseLocation;

        public MainForm()
        {
            // Store controls in fields for direct access
            lblFilePath = new Label { Text = "ファイル: 未選択", Dock = DockStyle.Fill, AutoSize = false, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            lstScripts = new ListBox { Dock = DockStyle.Fill, Height = 300 };
            txtCurrentScript = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new System.Drawing.Font("Consolas", 10), ReadOnly = true };
            txtNewScript = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new System.Drawing.Font("Consolas", 10) };
            
            InitializeComponent();
            
            fileWatcher = new FileSystemWatcher { NotifyFilter = NotifyFilters.LastWrite };
            fileWatcher.Changed += FileWatcher_Changed;
        }

        private void InitializeComponent()
        {
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
                Text = "X",
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
            pnlTitleBar.Controls.Add(btnMaximize);
            pnlTitleBar.Controls.Add(btnMinimize);
            pnlTitleBar.Controls.Add(btnClose);

            // Event Handlers for custom title bar
            btnClose.Click += (s, e) => this.Close();
            btnMinimize.Click += (s, e) => this.WindowState = FormWindowState.Minimized;
            btnMaximize.Click += (s, e) => {
                this.WindowState = this.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                btnMaximize.Text = this.WindowState == FormWindowState.Maximized ? "🗗" : "🗖"; // Restore/Maximize symbol
            };

            // Drag functionality
            pnlTitleBar.MouseDown += (s, e) => mouseLocation = e.Location;
            pnlTitleBar.MouseMove += (s, e) => {
                if (e.Button == MouseButtons.Left)
                {
                    this.Left += e.X - mouseLocation.X;
                    this.Top += e.Y - mouseLocation.Y;
                }
            };
            lblTitle.MouseDown += (s, e) => {
                // Propagate mouse down to parent to trigger drag
                pnlTitleBar.Capture = false;
                Message msg = Message.Create(pnlTitleBar.Handle, 0x00A1, (IntPtr)0x0002, IntPtr.Zero);
                this.DefWndProc(ref msg);
            };


            this.Text = "Stormworks Lua Script Replacer";
            this.Size = new System.Drawing.Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;

            var btnLoadXml = new Button { Text = "ビークルXMLを開く", Dock = DockStyle.Fill, Height = 40 };
            btnLoadXml.Click += BtnLoadXml_Click;
            
            lstScripts.SelectedIndexChanged += LstScripts_SelectedIndexChanged;

            var grpCurrentScript = new GroupBox { Text = "現在のスクリプト", Dock = DockStyle.Fill, Controls = { txtCurrentScript } };
            var grpNewScript = new GroupBox { Text = "新しいスクリプト", Dock = DockStyle.Fill, Controls = { txtNewScript } };

            var btnLoadLuaFile = new Button { Text = "Luaファイルを読み込む", Width = 150, Height = 40 };
            btnLoadLuaFile.Click += BtnLoadLuaFile_Click;

            var btnReplace = new Button { Text = "置換", Width = 100, Height = 40 };
            btnReplace.Click += BtnReplace_Click;

            var btnSave = new Button { Text = "XMLを保存", Width = 120, Height = 40 };
            btnSave.Click += BtnSave_Click;

            var btnSaveAs = new Button { Text = "名前を付けて保存", Width = 150, Height = 40 };
            btnSaveAs.Click += BtnSaveAs_Click;

            var btnSettings = new Button { Text = "検出設定", Width = 100, Height = 40 };
            btnSettings.Click += BtnSettings_Click;

            var pnlButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            pnlButtons.Controls.AddRange(new Control[] { btnLoadLuaFile, btnReplace, btnSave, btnSaveAs, btnSettings });

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));

            mainLayout.Controls.Add(btnLoadXml, 0, 0);
            mainLayout.SetColumnSpan(btnLoadXml, 2);
            mainLayout.Controls.Add(lblFilePath, 0, 1);
            mainLayout.SetColumnSpan(lblFilePath, 2);
            var scriptListPanel = new Panel { Dock = DockStyle.Fill, Controls = { lstScripts } };
            mainLayout.Controls.Add(scriptListPanel, 0, 1);
            mainLayout.SetColumnSpan(scriptListPanel, 2);
            mainLayout.Controls.Add(grpCurrentScript, 0, 2);
            mainLayout.Controls.Add(grpNewScript, 1, 2);
            mainLayout.Controls.Add(pnlButtons, 0, 3);
            mainLayout.SetColumnSpan(pnlButtons, 2);

            this.Controls.Add(mainLayout);
            this.Controls.Add(pnlTitleBar); // Add title bar to form
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
                .Where(e => e.Attribute("script")?.Value.Trim().StartsWith(scriptDetectionPrefix, StringComparison.OrdinalIgnoreCase) ?? false);

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
            lblFilePath.Text = $"ファイル: {currentFilePath}";
            
            lstScripts.Items.Clear();
            foreach (var script in luaScripts)
            {
                lstScripts.Items.Add(script.DisplayName);
            }
        }

        private void LstScripts_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (lstScripts.SelectedIndex < 0) return;

            var selectedScript = luaScripts[lstScripts.SelectedIndex];
            txtCurrentScript.Text = selectedScript.Script;

            if (string.IsNullOrEmpty(txtNewScript.Text))
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
                    txtNewScript.Text = File.ReadAllText(openFileDialog.FileName);
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
            if (lstScripts.SelectedIndex < 0)
            {
                MessageBox.Show("置換するスクリプトを選択してください。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtNewScript.Text))
            {
                MessageBox.Show("新しいスクリプトを入力してください。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selectedScript = luaScripts[lstScripts.SelectedIndex];
            selectedScript.Attribute.Value = txtNewScript.Text;
            selectedScript.Script = txtNewScript.Text;
            txtCurrentScript.Text = txtNewScript.Text;

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
            if (isReloading) return;
            isReloading = true;
            
            this.Invoke((Action)(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(100);
                    
                    int selectedIndex = lstScripts.SelectedIndex;
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
                    isReloading = false;
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
            using var settingsDialog = new SettingsDialog(scriptDetectionPrefix);
            if (settingsDialog.ShowDialog() == DialogResult.OK)
            {
                scriptDetectionPrefix = settingsDialog.DetectionPrefix;
                
                if (vehicleXml != null)
                {
                    ExtractLuaScripts();
                    UpdateUI();
                    MessageBox.Show($"検出条件を更新しました。\n{luaScripts.Count}個のスクリプトが見つかりました。",
                        "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
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
                Text = "検出するスクリプトの先頭コメントプレフィックスを設定してください。\n例: \"-- autochanger\" と入力すると、この文字列で始まるスクリプトのみが検出されます。",
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