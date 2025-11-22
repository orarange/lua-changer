using System;
using System.Collections.Generic;
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
        private List<LuaScriptNode> luaScripts;
        private FileSystemWatcher fileWatcher;
        private bool isReloading = false;

        public MainForm()
        {
            InitializeComponent();
            luaScripts = new List<LuaScriptNode>();
            fileWatcher = new FileSystemWatcher();
            fileWatcher.Changed += FileWatcher_Changed;
            fileWatcher.NotifyFilter = NotifyFilters.LastWrite;
        }

        private void InitializeComponent()
        {
            // フォームの初期化
            this.Text = "Stormworks Lua Script Replacer";
            this.Size = new System.Drawing.Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;

            // レイアウト
            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(10)
            };

            // ファイル選択ボタン
            var btnLoadXml = new Button
            {
                Text = "ビークルXMLを開く",
                Dock = DockStyle.Fill,
                Height = 40
            };
            btnLoadXml.Click += BtnLoadXml_Click;

            // 現在のファイルパス表示
            var lblFilePath = new Label
            {
                Text = "ファイル: 未選択",
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            lblFilePath.Name = "lblFilePath";

            // Luaスクリプト一覧
            var lstScripts = new ListBox
            {
                Dock = DockStyle.Fill,
                Height = 300
            };
            lstScripts.Name = "lstScripts";
            lstScripts.SelectedIndexChanged += LstScripts_SelectedIndexChanged;

            // スクリプトプレビュー（左）
            var grpCurrentScript = new GroupBox
            {
                Text = "現在のスクリプト",
                Dock = DockStyle.Fill
            };
            var txtCurrentScript = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Both,
                Font = new System.Drawing.Font("Consolas", 10),
                ReadOnly = true
            };
            txtCurrentScript.Name = "txtCurrentScript";
            grpCurrentScript.Controls.Add(txtCurrentScript);

            // 新しいスクリプト入力（右）
            var grpNewScript = new GroupBox
            {
                Text = "新しいスクリプト",
                Dock = DockStyle.Fill
            };
            var txtNewScript = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Both,
                Font = new System.Drawing.Font("Consolas", 10)
            };
            txtNewScript.Name = "txtNewScript";
            grpNewScript.Controls.Add(txtNewScript);

            // ボタンパネル
            var pnlButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight
            };

            var btnLoadLuaFile = new Button
            {
                Text = "Luaファイルを読み込む",
                Width = 150,
                Height = 40
            };
            btnLoadLuaFile.Click += BtnLoadLuaFile_Click;

            var btnReplace = new Button
            {
                Text = "置換",
                Width = 100,
                Height = 40
            };
            btnReplace.Click += BtnReplace_Click;

            var btnSave = new Button
            {
                Text = "XMLを保存",
                Width = 120,
                Height = 40
            };
            btnSave.Click += BtnSave_Click;

            var btnSaveAs = new Button
            {
                Text = "名前を付けて保存",
                Width = 150,
                Height = 40
            };
            btnSaveAs.Click += BtnSaveAs_Click;

            pnlButtons.Controls.AddRange(new Control[] { btnLoadLuaFile, btnReplace, btnSave, btnSaveAs });

            // レイアウト設定
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

            mainLayout.Controls.Add(btnLoadXml, 0, 0);
            mainLayout.SetColumnSpan(btnLoadXml, 2);

            mainLayout.Controls.Add(lblFilePath, 0, 1);
            mainLayout.SetColumnSpan(lblFilePath, 2);

            var scriptListPanel = new Panel { Dock = DockStyle.Fill };
            scriptListPanel.Controls.Add(lstScripts);
            mainLayout.Controls.Add(scriptListPanel, 0, 1);
            mainLayout.SetColumnSpan(scriptListPanel, 2);

            mainLayout.Controls.Add(grpCurrentScript, 0, 2);
            mainLayout.Controls.Add(grpNewScript, 1, 2);

            mainLayout.Controls.Add(pnlButtons, 0, 3);
            mainLayout.SetColumnSpan(pnlButtons, 2);

            this.Controls.Add(mainLayout);
        }

        private void BtnLoadXml_Click(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*";
                openFileDialog.Title = "ビークルXMLファイルを選択";

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
        }

        private void ExtractLuaScripts()
        {
            luaScripts.Clear();
            if (vehicleXml == null) return;

            // XMLからscript属性を持つすべての要素を検索
            var scriptElements = vehicleXml.Descendants()
                .Where(e => e.Attribute("script") != null && 
                            !string.IsNullOrWhiteSpace(e.Attribute("script")?.Value))
                .ToList();

            int index = 1;
            foreach (var element in scriptElements)
            {
                var scriptAttribute = element.Attribute("script");
                if (scriptAttribute == null) continue;
                
                var scriptContent = scriptAttribute.Value;
                
                // スクリプトの最初の2行を取得
                var lines = scriptContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                var firstLine = lines.Length > 0 ? lines[0].Trim() : "";
                var secondLine = lines.Length > 1 ? lines[1].Trim() : "";
                
                // -- autochangerで始まるコメントがあるスクリプトのみを対象とする
                if (!firstLine.StartsWith("-- autochanger", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // autochangerコメントがない場合はスキップ
                }

                // 識別子を取得: 1行目と2行目のコメントを組み合わせる
                string identifier = firstLine.Substring(2).Trim(); // "--"を除去
                
                // 2行目もコメントの場合は追加
                if (secondLine.StartsWith("--"))
                {
                    string secondComment = secondLine.Substring(2).Trim();
                    identifier += " " + secondComment;
                }
                
                var luaScript = new LuaScriptNode
                {
                    Element = element,
                    Attribute = scriptAttribute,
                    Index = index++,
                    Script = scriptContent,
                    NodePath = GetXPath(element),
                    DisplayName = identifier // "autochanger helicon test15" など
                };

                luaScripts.Add(luaScript);
            }
        }

        private string GetXPath(XElement element)
        {
            if (element == null) return "";
            
            var path = element.Name.LocalName;
            var current = element;
            
            while (current.Parent != null)
            {
                current = current.Parent;
                var siblings = current.Elements(element.Name).ToList();
                if (siblings.Count > 1)
                {
                    int index = siblings.IndexOf(element) + 1;
                    path = $"{current.Name.LocalName}/{element.Name.LocalName}[{index}]";
                }
                else
                {
                    path = $"{current.Name.LocalName}/{path}";
                }
            }
            
            return path;
        }

        private void UpdateUI()
        {
            var lblFilePath = this.Controls.Find("lblFilePath", true).FirstOrDefault() as Label;
            if (lblFilePath != null)
            {
                lblFilePath.Text = $"ファイル: {currentFilePath}";
            }

            var lstScripts = this.Controls.Find("lstScripts", true).FirstOrDefault() as ListBox;
            if (lstScripts != null)
            {
                lstScripts.Items.Clear();
                foreach (var script in luaScripts)
                {
                    lstScripts.Items.Add(script.DisplayName);
                }
            }
        }

        private void LstScripts_SelectedIndexChanged(object sender, EventArgs e)
        {
            var lstScripts = sender as ListBox;
            if (lstScripts == null || lstScripts.SelectedIndex < 0) return;

            var selectedScript = luaScripts[lstScripts.SelectedIndex];
            
            var txtCurrentScript = this.Controls.Find("txtCurrentScript", true).FirstOrDefault() as TextBox;
            if (txtCurrentScript != null)
            {
                txtCurrentScript.Text = selectedScript.Script;
            }

            var txtNewScript = this.Controls.Find("txtNewScript", true).FirstOrDefault() as TextBox;
            if (txtNewScript != null && string.IsNullOrEmpty(txtNewScript.Text))
            {
                txtNewScript.Text = selectedScript.Script;
            }
        }

        private void BtnLoadLuaFile_Click(object sender, EventArgs e)
        {
            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Lua files (*.lua)|*.lua|Text files (*.txt)|*.txt|All files (*.*)|*.*";
                openFileDialog.Title = "Luaスクリプトファイルを選択";

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string luaContent = File.ReadAllText(openFileDialog.FileName);
                        var txtNewScript = this.Controls.Find("txtNewScript", true).FirstOrDefault() as TextBox;
                        if (txtNewScript != null)
                        {
                            txtNewScript.Text = luaContent;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Luaファイルの読み込みに失敗しました:\n{ex.Message}",
                            "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnReplace_Click(object sender, EventArgs e)
        {
            var lstScripts = this.Controls.Find("lstScripts", true).FirstOrDefault() as ListBox;
            if (lstScripts == null || lstScripts.SelectedIndex < 0)
            {
                MessageBox.Show("置換するスクリプトを選択してください。", "警告", 
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var txtNewScript = this.Controls.Find("txtNewScript", true).FirstOrDefault() as TextBox;
            if (txtNewScript == null || string.IsNullOrWhiteSpace(txtNewScript.Text))
            {
                MessageBox.Show("新しいスクリプトを入力してください。", "警告",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selectedIndex = lstScripts.SelectedIndex;
            var selectedScript = luaScripts[selectedIndex];
            selectedScript.Attribute.Value = txtNewScript.Text;
            selectedScript.Script = txtNewScript.Text;

            // 現在のスクリプト表示を更新
            var txtCurrentScript = this.Controls.Find("txtCurrentScript", true).FirstOrDefault() as TextBox;
            if (txtCurrentScript != null)
            {
                txtCurrentScript.Text = txtNewScript.Text;
            }

            MessageBox.Show("スクリプトを置換しました。保存するには「XMLを保存」ボタンをクリックしてください。",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            if (vehicleXml == null || string.IsNullOrEmpty(currentFilePath))
            {
                MessageBox.Show("XMLファイルが読み込まれていません。", "警告",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                vehicleXml.Save(currentFilePath);
                MessageBox.Show("XMLファイルを保存しました。", "成功",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"XMLファイルの保存に失敗しました:\n{ex.Message}",
                    "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            
            // UIスレッドで実行
            this.Invoke(new Action(() =>
            {
                try
                {
                    // ファイルが書き込み中でないか確認するために少し待機
                    System.Threading.Thread.Sleep(100);
                    
                    var lstScripts = this.Controls.Find("lstScripts", true).FirstOrDefault() as ListBox;
                    int selectedIndex = lstScripts?.SelectedIndex ?? -1;

                    LoadXmlFile();

                    // 以前の選択状態を復元
                    if (lstScripts != null && selectedIndex >= 0 && selectedIndex < luaScripts.Count)
                    {
                        lstScripts.SelectedIndex = selectedIndex;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"ファイルの再読み込みに失敗しました:\n{ex.Message}",
                        "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    isReloading = false;
                }
            }));
        }

        private void BtnSaveAs_Click(object sender, EventArgs e)
        {
            if (vehicleXml == null)
            {
                MessageBox.Show("XMLファイルが読み込まれていません。", "警告",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*";
                saveFileDialog.Title = "XMLファイルを保存";
                saveFileDialog.FileName = Path.GetFileName(currentFilePath);

                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        vehicleXml.Save(saveFileDialog.FileName);
                        currentFilePath = saveFileDialog.FileName;
                        UpdateUI();
                        MessageBox.Show("XMLファイルを保存しました。", "成功",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"XMLファイルの保存に失敗しました:\n{ex.Message}",
                            "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
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
        public string NodePath { get; set; } = string.Empty;
    }
}