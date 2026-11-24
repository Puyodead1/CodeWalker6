using CodeWalker.Forms;
using CodeWalker.GameFiles;
using CodeWalker.Properties;
using CodeWalker.Tools;
using CodeWalker.World;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using WeifenLuo.WinFormsUI.Docking;

namespace CodeWalker
{
    public partial class ExploreForm : Form
    {
        private volatile bool Ready = false;

        private Dictionary<string, FileTypeInfo> FileTypes;
        private readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        private MainTreeFolder RootFolder;
        private List<MainTreeFolder> ExtraRootFolders = new List<MainTreeFolder>();
        private MainTreeFolder CurrentFolder;
        private List<MainListItem> CurrentFiles;
        private bool FirstRefreshed = false;
        private List<MainListItem> CopiedFiles = new List<MainListItem>();
        private bool DropFolderCreated;

        private Stack<MainTreeFolder> BackSteps = new Stack<MainTreeFolder>();
        private Stack<MainTreeFolder> ForwardSteps = new Stack<MainTreeFolder>();
        private bool HistoryNavigating = false;

        private int SortColumnIndex = 0;
        private SortOrder SortDirection = SortOrder.Ascending;
        private int PreviousPathColumnWidth = 0;

        public volatile bool Searching = false;
        private MainTreeFolder SearchResults;

        private List<RpfFile> AllRpfs { get; set; }
        private GameFileCache FileCache { get; set; } = GameFileCacheFactory.Create();
        private object FileCacheSyncRoot = new object();

        public bool EditMode { get; private set; } = false;

        public ThemeBase Theme { get; private set; }


        public ExploreForm()
        {
            InitializeComponent();

            SetTheme(Settings.Default.ExplorerWindowTheme, false);

            ShowMainListViewPathColumn(false);

            LoadSettings();

            GTAFolder.UpdateEnhancedFormTitle(this);
        }

        private void SetTheme(string themestr, bool changing = true)
        {
            //string configFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "DockPanel.temp.config");
            //MainDockPanel.SaveAsXml(configFile);
            //CloseAllContents();

            foreach (ToolStripMenuItem menu in ViewThemeMenu.DropDownItems)
            {
                menu.Checked = false;
            }

            Theme = null;
            var version = VisualStudioToolStripExtender.VsVersion.Vs2015;

            switch (themestr)
            {
                default:
                case "Windows":
                    //Theme = new VS2005Theme();
                    ViewThemeWindowsMenu.Checked = true;
                    version = VisualStudioToolStripExtender.VsVersion.Unknown;
                    if (changing)
                    {
                        MessageBox.Show("Please reopen RPF Explorer to change back to Windows theme.");
                    }
                    break;
                case "Blue":
                    Theme = new VS2015BlueTheme();
                    ViewThemeBlueMenu.Checked = true;
                    break;
                case "Light":
                    Theme = new VS2015LightTheme();
                    ViewThemeLightMenu.Checked = true;
                    break;
                case "Dark":
                    Theme = new VS2015DarkTheme();
                    ViewThemeDarkMenu.Checked = true;
                    break;
            }

            if (changing)
            {
                Settings.Default.ExplorerWindowTheme = themestr;
                Settings.Default.Save();
            }


            //Theme.Extender.FloatWindowFactory = new ExplorerFloatWindowFactory();
            //MainDockPanel.Theme = Theme;

            if (Theme != null)
            {
                VSExtender.SetStyle(MainMenu, version, Theme);
                VSExtender.SetStyle(MainToolbar, version, Theme);
                VSExtender.SetStyle(MainStatusBar, version, Theme);

                FormTheme.SetTheme(this, Theme);

                MainSplitContainer.BackColor = Theme.ColorPalette.MainWindowActive.Background;
            }


            //if (File.Exists(configFile)) MainDockPanel.LoadFromXml(configFile, m_deserializeDockContent);
        }

        private void LoadSettings()
        {
            var s = Settings.Default;

            OptionsStartInEditModeMenu.Checked = s.RPFExplorerStartInEditMode;
            OptionsStartInFolderValueMenu.Text = string.IsNullOrEmpty(s.RPFExplorerStartFolder) ? "(Default)" : s.RPFExplorerStartFolder;

            var folders = s.RPFExplorerExtraFolders?.Split('\n');
            if (folders != null)
            {
                foreach (var folder in folders)
                {
                    var folderPath = folder?.Trim();
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        var root = new MainTreeFolder();
                        root.FullPath = folderPath;
                        root.Path = folderPath;
                        root.Name = Path.GetFileName(Path.GetDirectoryName(folderPath));
                        root.IsExtraFolder = true;
                        ExtraRootFolders.Add(root);
                    }
                }
            }
        }
        private void SaveSettings()
        {
            var extrafolders = new StringBuilder();
            foreach (var folder in ExtraRootFolders)
            {
                if (extrafolders.Length > 0) extrafolders.Append("\n");
                extrafolders.Append(folder.FullPath);
            }
            Settings.Default.RPFExplorerExtraFolders = extrafolders.ToString();

            Settings.Default.Save();
        }

        private void Init()
        {
            //called from ExploreForm_Load

            InitFileTypes();

            // This is probably not necessary now that the GTA folder is checked 
            // in the Program.cs when the game is initiated, but we will leave it 
            // here for now to make sure 
            if(!GTAFolder.UpdateGTAFolder(true))
            {
                Close();
                return;
            }


            if (GTAFolder.IsGta6)
            {
                InitGta6();
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    GTA5Keys.LoadFromPath(GTAFolder.CurrentGTAFolder, GTAFolder.IsGen9, Settings.Default.Key);
                }
                catch
                {
                    UpdateStatus("Unable to load gta5.exe!");
                    return;
                }

                RefreshMainTreeView();

                UpdateStatus("Scan complete.");

                InitFileCache();

                while (!IsDisposed) //run the file cache content thread until the form exits.
                {
                    if (FileCache.IsInited)
                    {
                        FileCache.BeginFrame();

                        bool fcItemsPending = FileCache.ContentThreadProc();

                        if (!fcItemsPending)
                        {
                            Thread.Sleep(10);
                        }
                    }
                    else
                    {
                        Thread.Sleep(20);
                    }
                }
            });
        }

        private void InitGta6()
        {
            //GTA VI: keys and cipher tables come from the game exe; no GTA V file cache
            var exe = GTAFolder.FindGta6Exe();
            if (exe == null)
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = "Select the GTA VI executable (game_scarlett_master.exe) to read the RPF9 keys from";
                    ofd.Filter = "Executable (*.exe)|*.exe";
                    if (ofd.ShowDialog(this) != DialogResult.OK) { UpdateStatus("GTA VI: no game exe selected."); return; }
                    exe = ofd.FileName;
                }
                GTAFolder.SaveGta6Exe(exe);
            }
            var names = GTAFolder.FindGta6Names();
            Task.Run(() =>
            {
                try
                {
                    UpdateStatus("Loading GTA VI keys from " + exe + "...");
                    Rpf9Keys.Load(exe, names);
                    GTAFolder.Gta6Exe = exe;
                }
                catch (Exception ex)
                {
                    UpdateStatus("Unable to load GTA VI keys: " + ex.Message);
                    return;
                }
                UpdateStatus(string.Format("{0} names, {1} TOC keys, {2} data keys. Scanning...", Rpf9Keys.Names.Count, Rpf9Keys.TocCiphers.Count, Rpf9Keys.DataCiphers.Count));

                RefreshMainTreeView();

                UpdateStatus("Scan complete.");
            });
        }

        private void InitFileCache()
        {
            if (GTAFolder.IsGta6) return;
            Task.Run(() =>
            {
                lock (FileCacheSyncRoot)
                {
                    if (!FileCache.IsInited)
                    {
                        UpdateStatus("Loading file cache...");
                        var allRpfs = AllRpfs;
                        FileCache.Init(UpdateStatus, UpdateErrorLog, allRpfs); //inits main dicts and archetypes only...

                        UpdateStatus("Loading materials...");
                        BoundsMaterialTypes.Init(FileCache);

                        UpdateStatus("Loading scenario types...");
                        Scenarios.EnsureScenarioTypes(FileCache);

                        UpdateStatus("File cache loaded.");
                    }
                }
            });
        }
        public GameFileCache GetFileCache()
        {
            lock (FileCacheSyncRoot)
            {
                if (FileCache.IsInited) return FileCache;
            }
            InitFileCache(); //if we got here, it's not inited yet - init it!
            return FileCache; //return it even though it's probably not inited yet..
        }

        private void InitFileTypes()
        {
            FileTypes = new Dictionary<string, FileTypeInfo>();
            InitFileType(".rpf", "Rage Package File", 3);
            InitFileType("", "File", 4);
            InitFileType(".dat", "Data File", 4);
            InitFileType(".cab", "CAB File", 4);
            InitFileType(".txt", "Text File", 5, FileTypeAction.ViewText);
            InitFileType(".gxt2", "Global Text Table", 5, FileTypeAction.ViewGxt);
            InitFileType(".log", "LOG File", 5, FileTypeAction.ViewText);
            InitFileType(".ini", "Config Text", 5, FileTypeAction.ViewText);
            InitFileType(".vdf", "Steam Script File", 5, FileTypeAction.ViewText);
            InitFileType(".sps", "Shader Preset", 5, FileTypeAction.ViewText);
            InitFileType(".ugc", "User-Generated Content", 5, FileTypeAction.ViewText);
            InitFileType(".xml", "XML File", 6, FileTypeAction.ViewXml);
            InitFileType(".meta", "Metadata (XML)", 6, FileTypeAction.ViewXml);
            InitFileType(".ymt", "Metadata (Binary)", 6, FileTypeAction.ViewYmt, true);
            InitFileType(".pso", "Metadata (PSO)", 6, FileTypeAction.ViewJPso, true);
            InitFileType(".gfx", "Scaleform Flash", 7);
            InitFileType(".ynd", "Path Nodes", 8, FileTypeAction.ViewYnd, true);
            InitFileType(".ynv", "Nav Mesh", 9, FileTypeAction.ViewModel, true);
            InitFileType(".yvr", "Vehicle Record", 9, FileTypeAction.ViewYvr, true);
            InitFileType(".ywr", "Waypoint Record", 9, FileTypeAction.ViewYwr, true);
            InitFileType(".fxc", "Compiled Shaders", 9, FileTypeAction.ViewFxc, true);
            InitFileType(".yed", "Expression Dictionary", 9, FileTypeAction.ViewYed, true);
            InitFileType(".yld", "Cloth Dictionary", 9, FileTypeAction.ViewYld, true);
            InitFileType(".yfd", "Frame Filter Dictionary", 9, FileTypeAction.ViewYfd, true);
            InitFileType(".asi", "ASI Plugin", 9);
            InitFileType(".dll", "Dynamic Link Library", 9);
            InitFileType(".exe", "Executable", 10);
            InitFileType(".yft", "Fragment", 11, FileTypeAction.ViewModel, true);
            InitFileType(".ydr", "Drawable", 11, FileTypeAction.ViewModel, true);
            InitFileType(".ydd", "Drawable Dictionary", 12, FileTypeAction.ViewModel, true);
            InitFileType(".cut", "Cutscene", 12, FileTypeAction.ViewCut, true);
            InitFileType(".ysc", "Script", 13);
            InitFileType(".ymf", "Manifest", 14, FileTypeAction.ViewYmf, true);
            InitFileType(".bik", "Bink Video", 15);
            InitFileType(".jpg", "JPEG Image", 16);
            InitFileType(".jpeg", "JPEG Image", 16);
            InitFileType(".gif", "GIF Image", 16);
            InitFileType(".png", "Portable Network Graphics", 16);
            InitFileType(".dds", "DirectDraw Surface", 16);
            InitFileType(".ytd", "Texture Dictionary", 16, FileTypeAction.ViewYtd, true);
            InitFileType(".mrf", "Move Network File", 18, FileTypeAction.ViewMrf, true);
            InitFileType(".ycd", "Clip Dictionary", 18, FileTypeAction.ViewYcd, true);
            InitFileType(".ypt", "Particle Effect", 18, FileTypeAction.ViewModel, true);
            InitFileType(".ybn", "Static Collisions", 19, FileTypeAction.ViewModel, true);
            InitFileType(".ide", "Item Definitions", 20, FileTypeAction.ViewText);
            InitFileType(".ytyp", "Archetype Definitions", 20, FileTypeAction.ViewYtyp, true);
            InitFileType(".ymap", "Map Data", 21, FileTypeAction.ViewYmap, true);
            InitFileType(".ipl", "Item Placements", 21, FileTypeAction.ViewText);
            InitFileType(".awc", "Audio Wave Container", 22, FileTypeAction.ViewAwc, true);
            InitFileType(".rel", "Audio Data (REL)", 23, FileTypeAction.ViewRel, true);
            InitFileType(".nametable", "Name Table", 5, FileTypeAction.ViewNametable);
            InitFileType(".ypdb", "Pose Matcher Database", 9, FileTypeAction.ViewYpdb, true);

            InitSubFileType(".dat", "cache_y.dat", "Cache File", 6, FileTypeAction.ViewCacheDat, true);
            InitSubFileType(".dat", "heightmap.dat", "Heightmap", 6, FileTypeAction.ViewHeightmap, true);
            InitSubFileType(".dat", "heightmapheistisland.dat", "Heightmap", 6, FileTypeAction.ViewHeightmap, true);
            InitSubFileType(".dat", "distantlights.dat", "Distant Lights", 6, FileTypeAction.ViewDistantLights);
            InitSubFileType(".dat", "distantlights_hd.dat", "Distant Lights", 6, FileTypeAction.ViewDistantLights);

            if (GTAFolder.IsGta6) InitGta6FileTypes();
        }
        private void InitGta6FileTypes()
        {
            //GTA VI (RPF9) extensions, from the exe's extension id tables. GTA V viewers don't understand these formats.
            InitFileType(".mrf", "Move Network File (GTA VI)", 18);
            InitFileType(".cut", "Cutscene (GTA VI)", 12);
            InitFileType(".gfx", "Scaleform Flash", 7);
            InitFileType(".zmf", "Manifest", 14);
            InitFileType(".zdr", "Drawable", 11, FileTypeAction.ViewModel);
            InitFileType(".zft", "Fragment", 11, FileTypeAction.ViewModel);
            InitFileType(".zdd", "Drawable Dictionary", 12, FileTypeAction.ViewModel);
            InitFileType(".ztd", "Texture Dictionary", 16, FileTypeAction.ViewYtd);
            InitFileType(".ttpf", "Texture Pack (TTDB)", 16, FileTypeAction.ViewYtd);
            InitFileType(".zbn", "Static Collisions", 19);
            InitFileType(".zbd", "Bounds Dictionary", 19);
            InitFileType(".zpd", "Physics Dictionary", 19);
            InitFileType(".zbs", "Blend Shapes", 9);
            InitFileType(".zsd", "Shader Data", 9);
            InitFileType(".zmt", "Metadata (PSIN / PRD0)", 6, FileTypeAction.ViewXml, true);
            InitFileType(".zsc", "Script", 13);
            InitFileType(".zcs", "Compiled Shaders", 9);
            InitFileType(".zdatb", "Data", 9);
            InitFileType(".zbdr", "Drawable (ZBDR)", 11);
            InitFileType(".zbdd", "Drawable Dictionary (ZBDD)", 12);
            InitFileType(".zbft", "Fragment (ZBFT)", 11);
            InitFileType(".zvert", "Vertex Data", 9);
            InitFileType(".zcd", "Clip Dictionary", 18, FileTypeAction.ViewYcd);
            InitFileType(".zld", "Cloth Dictionary", 9);
            InitFileType(".zpmd", "Parameterized Motion Dictionary", 18);
            InitFileType(".zpm", "Parameterized Motion", 18);
            InitFileType(".zed", "Expression Dictionary", 9);
            InitFileType(".zpt", "Particle Effect", 18);
            InitFileType(".zmap", "Map Data", 21);
            InitFileType(".ztyp", "Archetype Definitions (PRD0)", 20, FileTypeAction.ViewXml, true);
            InitFileType(".zch", "Clip Hierarchy", 18);
            InitFileType(".zldb", "Level Database", 9);
            InitFileType(".zjd", "Joint Data", 9);
            InitFileType(".zad", "Animation Data", 18);
            InitFileType(".znv", "Nav Mesh", 9);
            InitFileType(".znvs", "Nav Mesh Set", 9);
            InitFileType(".zpl", "Point List", 9);
            InitFileType(".znd", "Path Nodes", 8);
            InitFileType(".zvr", "Vehicle Record", 9);
            InitFileType(".zwr", "Waypoint Record", 9);
            InitFileType(".znh", "Nav Hierarchy", 9);
            InitFileType(".zfd", "Frame Filter Dictionary", 9);
            InitFileType(".zas", "Asset (PRD0)", 20);
            InitFileType(".zsm", "Secondary Motion", 9, FileTypeAction.ViewText);
            InitFileType(".zscript", "Script", 13);
            InitFileType(".zascript", "Script", 13);
            InitFileType(".zsma", "ZSMA", 9);
            InitFileType(".iasf", "IASF", 9);
            InitFileType(".zfc", "Metadata (PSIN, ZFC)", 6, FileTypeAction.ViewXml, true);
            InitFileType(".zlfc", "Metadata (PSIN, ZLFC)", 6, FileTypeAction.ViewXml, true);
            InitFileType(".zcg", "ZCG", 9);
            InitFileType(".zasd", "ZASD", 9);
            InitFileType(".rdb", "RDB", 9);
            InitFileType(".zfbs", "ZFBS", 9);
            InitFileType(".zcps", "ZCPS", 9);
            InitFileType(".zsp", "ZSP", 9);
            InitFileType(".ztsl", "ZTSL", 9);
            InitFileType(".zmmd", "ZMMD", 9);
            InitFileType(".zfid", "Face Morph Data", 9);
            InitFileType(".zmsk", "Mask", 9);
            InitFileType(".zhdat", "ZHDAT", 9);
            InitFileType(".znmd", "ZNMD", 9);
            InitFileType(".zsmm", "ZSMM", 9);
            InitFileType(".zascd", "Asset (PRD0, ZASCD)", 20);
            InitFileType(".zrd", "ZRD", 9);
            InitFileType(".zsmam", "Secondary Motion Asset Map", 9, FileTypeAction.ViewText);
            InitFileType(".zldbmt", "Level Database Metadata (PRD0)", 20, FileTypeAction.ViewXml, true);
            InitFileType(".zkit", "Kit (PRD0)", 20, FileTypeAction.ViewXml, true);
            InitFileType(".zcgc", "ZCGC", 9);
            InitFileType(".zcov", "ZCOV", 9);
            InitFileType(".bin", "File", 4);
        }
        private void InitFileType(string ext, string name, int imgidx, FileTypeAction defaultAction = FileTypeAction.ViewHex, bool xmlConvertible = false)
        {
            var ft = new FileTypeInfo(ext, name, imgidx, defaultAction, xmlConvertible);
            FileTypes[ext] = ft;
        }
        private void InitSubFileType(string ext, string subext, string name, int imgidx, FileTypeAction defaultAction = FileTypeAction.ViewHex, bool xmlConvertible = false)
        {
            FileTypeInfo pti = null;
            if (FileTypes.TryGetValue(ext, out pti))
            {
                var ft = new FileTypeInfo(subext, name, imgidx, defaultAction, xmlConvertible);
                pti.AddSubType(ft);
            }
        }
        public FileTypeInfo GetFileType(string fn)
        {
            if (fn.IndexOfAny(InvalidFileNameChars) != -1)
            {
                return FileTypes[""];
            }

            var ext = Path.GetExtension(fn).ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext))
            {
                FileTypeInfo ft;
                if (FileTypes.TryGetValue(ext, out ft))
                {
                    if (ft.SubTypes != null)
                    {
                        var fnl = fn.ToLowerInvariant();
                        foreach (var sft in ft.SubTypes)
                        {
                            if (fnl.EndsWith(sft.Extension))
                            {
                                return sft;
                            }
                        }
                    }
                    return ft;
                }
                else
                {
                    ft = new FileTypeInfo(ext, ext.Substring(1).ToUpperInvariant() + " File", 4, FileTypeAction.ViewHex, false);
                    FileTypes[ft.Extension] = ft; //save it for later!
                    return ft;
                }
            }
            else
            {
                return FileTypes[""];
            }
        }



        public void ConfigureGame()
        {
            var result = GTAFolder.UpdateGTAFolder(false, false);
            if (result)
            {
                MessageBox.Show("Please restart CodeWalker to use the new folder.");
            }
        }




        public void UpdateStatus(string text)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => { UpdateStatus(text); }));
                }
                else
                {
                    StatusLabel.Text = text;
                }
            }
            catch { }
        }
        public void UpdateErrorLog(string text)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => { UpdateErrorLog(text); }));
                }
                else
                {
                    //StatusLabel.Text = text;
                }
            }
            catch { }
        }


        public void Navigate(MainTreeFolder f)
        {
            if (!Ready) return;
            if (f == CurrentFolder) return; //already there!
            if (f.IsSearchResults)
            {
                AddMainTreeViewRoot(f); //add the current search result node

                TreeNode sr = FindTreeNode(f, null);
                if (sr != null)
                {
                    MainTreeView.SelectedNode = sr; //navigate to the new search results node
                }

                foreach (TreeNode tn in MainTreeView.Nodes)
                {
                    var tnf = tn.Tag as MainTreeFolder;
                    if ((tn != sr) && (tnf != null) && (tnf.IsSearchResults))
                    {
                        MainTreeView.Nodes.Remove(tn); //remove existing search result node
                        break;
                    }
                }

                return;
            }
            List<MainTreeFolder> hierarchy = new List<MainTreeFolder>();
            var pf = f;
            while (pf != null)
            {
                hierarchy.Add(pf);
                pf = pf.Parent;
            }
            TreeNode n = null;
            for (int i = hierarchy.Count - 1; i >= 0; i--)
            {
                n = FindTreeNode(hierarchy[i], n);
            }
            if (n != null)
            {
                if (MainTreeView.SelectedNode != n)
                {
                    MainTreeView.SelectedNode = n; //this will trigger everything else
                }
            }
            else
            {
                Navigate(f.Path); //try again using the path string...
                //NavigateFailed(f.Path); //unable to navigate...
            }
        }
        public void Navigate(string path)
        {
            if (!Ready) return;
            var pathl = path.ToLowerInvariant().Replace('/', '\\');
            if ((CurrentFolder != null) && (CurrentFolder.Path.ToLowerInvariant() == pathl)) return; //already there
            var root = RootFolder;
            var relpath = pathl;
            if (pathl.Contains(':'))
            {
                foreach (var extf in ExtraRootFolders)
                {
                    if (extf == null) continue;
                    if (pathl.StartsWith(extf.FullPath, StringComparison.InvariantCultureIgnoreCase))
                    {
                        root = extf;
                        relpath = pathl.Substring(extf.FullPath.Length);
                        break;
                    }
                }
            }
            
            var hierarchy = relpath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            TreeNode n = root.TreeNode;// FindTreeNode("gta v", null);
            if (!string.IsNullOrEmpty(path))
            {
                for (int i = 0; i < hierarchy.Length; i++)
                {
                    n = FindTreeNode(hierarchy[i], n);
                }
            }
            if (n != null)
            {
                if (MainTreeView.SelectedNode != n)
                {
                    MainTreeView.SelectedNode = n; //this will trigger everything else
                }
            }
            else
            {
                NavigateFailed(path); //unable to navigate...
            }
        }
        private void NavigateFailed(string path)
        {
            MessageBox.Show("Unable to navigate to \"" + path + "\".");
        }
        private void NavigateComplete(MainTreeFolder prevFolder)
        {
            //called after the CurrentFolder and CurrentFiles have changed. 

            UpdateNavigateUI();
            EnsureEditModeWarning();

            if (!HistoryNavigating) //only do this if not currently navigating forward or back
            {
                if (prevFolder != null)
                {
                    ForwardSteps.Clear();
                    BackSteps.Push(prevFolder);
                }
                UpdateHistoryUI();
            }

            GoButton.Enabled = true;
            RefreshButton.Enabled = true;
            SearchButton.Enabled = true;
            SearchButton.Checked = false;
            SearchFilterButton.Checked = false;
            EditModeButton.Enabled = true;
        }

        public void GoUp(MainTreeFolder toFolder = null)
        {
            var fld = (toFolder == null) ? CurrentFolder?.Parent : toFolder;
            if (fld == null) return;
            Navigate(fld);
        }
        public void GoBack(MainTreeFolder toFolder = null)
        {
            if (BackSteps.Count == 0) return;
            var s = BackSteps.Pop();
            ForwardSteps.Push(CurrentFolder);
            while ((toFolder != null) && (s != toFolder) && (BackSteps.Count > 0))
            {
                ForwardSteps.Push(s);
                s = BackSteps.Pop();
            }
            HistoryNavigating = true;
            Navigate(s);
            HistoryNavigating = false;
            UpdateHistoryUI();
        }
        public void GoForward(MainTreeFolder toFolder = null)
        {
            if (ForwardSteps.Count == 0) return;
            var s = ForwardSteps.Pop();
            BackSteps.Push(CurrentFolder);
            while ((toFolder != null) && (s != toFolder) && (ForwardSteps.Count > 0))
            {
                BackSteps.Push(s);
                s = ForwardSteps.Pop();
            }
            HistoryNavigating = true;
            Navigate(s);
            HistoryNavigating = false;
            UpdateHistoryUI();
        }

        private void UpdateNavigateUI()
        {
            UpButton.DropDownItems.Clear();
            var pf = CurrentFolder?.Parent;
            int i = 0;
            while (pf != null)
            {
                var button = UpButton.DropDownItems.Add(pf.GetToolText());
                button.Tag = pf;
                button.Click += UpListButton_Click;
                pf = pf.Parent;
                if (i >= 10) break;
            }
            UpButton.Enabled = (UpButton.DropDownItems.Count > 0);
        }
        private void UpdateHistoryUI()
        {
            BackButton.DropDownItems.Clear();
            ForwardButton.DropDownItems.Clear();
            int i = 0;
            foreach (var step in BackSteps)
            {
                var button = BackButton.DropDownItems.Add(step.GetToolText());
                button.Tag = step;
                button.Click += BackListButton_Click;
                i++;
                if (i >= 10) break;
            }
            i = 0;
            foreach (var step in ForwardSteps)
            {
                var button = ForwardButton.DropDownItems.Add(step.GetToolText());
                button.Tag = step;
                button.Click += ForwardListButton_Click;
                i++;
                if (i >= 10) break;
            }
            BackButton.Enabled = (BackSteps.Count > 0);
            ForwardButton.Enabled = (ForwardSteps.Count > 0);
        }
        private void UpdateSelectionUI()
        {
            var ic = MainListView.VirtualListSize;
            var sc = MainListView.SelectedIndices.Count;
            var str = ic.ToString() + " item" + ((ic != 1) ? "s" : "") + " shown";
            bool isitem = false;
            bool isfile = false;
            bool issearch = CurrentFolder?.IsSearchResults ?? false;
            bool canview = false;
            bool canedit = false;
            bool canexportxml = false;
            bool canimport = EditMode && !issearch;// && (CurrentFolder?.RpfFolder != null);
            bool canpaste = EditMode && (CopiedFiles.Count > 0);

            if (sc != 0)
            {
                long bc = 0;
                if (CurrentFiles != null)
                {
                    foreach (int idx in MainListView.SelectedIndices)
                    {
                        if ((idx < 0) || (idx >= CurrentFiles.Count)) continue;
                        var file = CurrentFiles[idx];
                        if ((file.Folder == null) || (file.Folder.RpfFile != null))
                        {
                            bc += file.FileSize;
                        }

                        isitem = true;
                        isfile = isfile || (file.Folder == null);
                        canview = canview || CanViewFile(file);
                        canexportxml = canexportxml || CanExportXml(file);
                        canedit = EditMode && !issearch;
                    }
                }
                str += ", " + sc.ToString() + " selected";
                if (bc > 0)
                {
                    str += ", " + TextUtil.GetBytesReadable(bc);
                }
            }
            UpdateStatus(str);



            EditViewMenu.Enabled = canview;
            EditViewHexMenu.Enabled = isfile;

            EditExportXmlMenu.Enabled = canexportxml;
            EditExtractRawMenu.Enabled = isfile;

            EditImportRawMenu.Visible = canimport;
            EditImportFbxMenu.Visible = canimport;
            EditImportXmlMenu.Visible = canimport;
            EditImportMenuSeparator.Visible = canimport;

            EditCopyMenu.Enabled = isfile;
            EditCopyPathMenu.Enabled = isitem;
            EditPasteMenu.Enabled = canpaste;
            EditPasteMenu.Visible = EditMode;

            EditRenameMenu.Visible = canedit;
            EditDeleteMenu.Visible = canedit;
            EditEditModeMenuSeparator.Visible = canedit;

        }

        private TreeNode FindTreeNode(MainTreeFolder f, TreeNode parent)
        {
            var tnc = (parent != null) ? parent.Nodes : MainTreeView.Nodes;
            foreach (TreeNode node in tnc)
            {
                if (node.Tag == f)
                {
                    return node;
                }
            }
            return null;
        }
        private TreeNode FindTreeNode(string text, TreeNode parent)
        {
            var tnc = (parent != null) ? parent.Nodes : MainTreeView.Nodes;
            foreach (TreeNode node in tnc)
            {
                if (node.Text.ToLowerInvariant() == text)
                {
                    return node;
                }
            }
            return null;
        }



        private void RefreshMainTreeView()
        {
            Ready = false;
            AllRpfs = null;
            gta6Textures?.Dispose();   // reopened on the next model view, picking up new packs
            gta6Textures = null;
            gta6Index = null;          // rebuilt from the new archives on first use
            gta6ClipCache.Clear();

            ClearMainTreeView();

            UpdateStatus("Scanning...");

            var root = new MainTreeFolder();
            root.FullPath = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();
            root.Path = "";
            root.Name = GTAFolder.IsGta6 ? "GTA VI" : "GTA V";
            RootFolder = root;

            RefreshMainTreeViewRoot(root);


            var remFolders = new List<MainTreeFolder>();

            foreach (var extraroot in ExtraRootFolders)
            {
                extraroot.Clear();

                if (Directory.Exists(extraroot.FullPath))
                {
                    RefreshMainTreeViewRoot(extraroot, true);
                }
                else
                {
                    remFolders.Add(extraroot);
                }
            }

            foreach (var remFolder in remFolders)
            {
                ExtraRootFolders.Remove(remFolder);
            }


            Ready = true;

            MainTreeViewRefreshComplete();
        }
        private void RefreshMainTreeViewRoot(MainTreeFolder f, bool extra = false)
        {
            var allRpfs = new List<RpfFile>();
            var fullPath = f.FullPath;
            var subPath = f.Path;
            var allpaths = Directory.GetFileSystemEntries(fullPath, "*", SearchOption.AllDirectories);
            var nodes = new Dictionary<string, MainTreeFolder>();

            foreach (var path in allpaths)
            {
                var relpath = path.Replace(fullPath, "");
                var filepathl = path.ToLowerInvariant();

                var isFile = File.Exists(path); //could be a folder

                UpdateStatus("Scanning " + relpath + "...");

                MainTreeFolder parentnode = null, prevnode = null, node = null;
                var prevnodepath = "";
                var idx = isFile ? relpath.LastIndexOf('\\') : relpath.Length;
                while (idx > 0) //create the folder tree nodes and build up the hierarchy
                {
                    var parentpath = relpath.Substring(0, idx);
                    var parentidx = parentpath.LastIndexOf('\\');
                    var parentname = parentpath.Substring(parentidx + 1);
                    var exists = nodes.TryGetValue(parentpath, out node);
                    if (!exists)
                    {
                        node = CreateRootDirTreeFolder(parentname, subPath + parentpath, fullPath + parentpath);
                        nodes[parentpath] = node;
                    }
                    if (parentnode == null)
                    {
                        parentnode = node;
                    }
                    if (prevnode != null)
                    {
                        node.AddChild(prevnode);
                    }
                    prevnode = node;
                    prevnodepath = parentpath;
                    idx = relpath.LastIndexOf('\\', idx - 1);
                    if (exists) break;
                    if (idx < 0)
                    {
                        f.AddChild(node);
                    }
                }

                if (isFile)
                {
                    if (filepathl.EndsWith(".rpf")) //add RPF nodes
                    {
                        RpfFile rpf = new RpfFile(path, relpath);

                        rpf.ScanStructure(UpdateStatus, UpdateErrorLog);

                        if (rpf.LastException != null) //incase of corrupted rpf (or renamed NG encrypted RPF)
                        {
                            continue;
                        }

                        if (extra)
                        {
                            relpath = path;
                        }

                        node = CreateRpfTreeFolder(rpf, relpath, path);

                        RecurseMainTreeViewRPF(node, allRpfs, extra ? f.Path : null);

                        if (parentnode != null)
                        {
                            parentnode.AddChild(node);
                        }
                        else
                        {
                            f.AddChild(node);
                        }
                    }
                    else
                    {
                        if (parentnode != null)
                        {
                            parentnode.AddFile(path);
                        }
                        else
                        {
                            f.AddFile(path);
                        }
                    }
                }
            }


            AddMainTreeViewRoot(f);

            if (f.Children != null)
            {
                f.Children.Sort((n1, n2) => n1.Name.CompareTo(n2.Name));

                foreach (var node in f.Children)
                {
                    AddMainTreeViewNode(node);
                }
            }

            if (AllRpfs == null)
            {
                AllRpfs = allRpfs;
            }

        }
        private void RecurseMainTreeViewRPF(MainTreeFolder f, List<RpfFile> allRpfs, string rootpath = null)
        {
            var gamepath = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();
            if (rootpath == null)
            {
                rootpath = gamepath;
            }

            var fld = f.RpfFolder;
            if (fld != null)
            {
                if (fld.Directories != null)
                {
                    foreach (var dir in fld.Directories)
                    {
                        var relpath = dir.Path.Substring(fld.Path.Length);
                        var fullpath = f.FullPath + relpath;
                        var dirpath = dir.Path;
                        if (fullpath.StartsWith(gamepath, StringComparison.InvariantCultureIgnoreCase) == false)
                        {
                            dirpath = fullpath;
                        }
                        var dtnf = CreateRpfDirTreeFolder(dir, dirpath, fullpath);
                        f.AddChild(dtnf);
                        RecurseMainTreeViewRPF(dtnf, allRpfs, rootpath);
                    }
                }
            }

            var rpf = f.RpfFile;
            if (rpf != null)
            {
                allRpfs.Add(rpf);

                if (rpf.Children != null)
                {
                    foreach (var child in rpf.Children)
                    {
                        var cpath = rootpath + child.Path;
                        var ctnf = CreateRpfTreeFolder(child, (rootpath != gamepath) ? cpath : child.Path, cpath);
                        f.AddChildToHierarchy(ctnf);
                        RecurseMainTreeViewRPF(ctnf, allRpfs, rootpath);
                    }
                }

                //JenkIndex.Ensure(rpf.Name);
                if (rpf.AllEntries != null)
                {
                    foreach (RpfEntry entry in rpf.AllEntries)
                    {
                        if (string.IsNullOrEmpty(entry.NameLower)) continue;
                        var shortnamel = entry.GetShortNameLower();
                        JenkIndex.Ensure(shortnamel);
                        entry.ShortNameHash = JenkHash.GenHash(shortnamel);
                        entry.NameHash = JenkHash.GenHash(entry.NameLower);
                    }
                }
            }
        }
        private void ClearMainTreeView()
        {
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => { ClearMainTreeView(); }));
                }
                else
                {
                    GoButton.Enabled = false;
                    UpButton.Enabled = false;
                    BackButton.Enabled = false;
                    ForwardButton.Enabled = false;
                    RefreshButton.Enabled = false;
                    SearchButton.Enabled = false;
                    EditModeButton.Enabled = false;
                    MainTreeView.Nodes.Clear();
                    MainListView.VirtualListSize = 0; //also clear the list view...
                }
            }
            catch { }
        }
        private void AddMainTreeViewRoot(MainTreeFolder f)
        {
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => { AddMainTreeViewRoot(f); }));
                }
                else
                {
                    int imgIndex = 1; //FOLDER imageIndex
                    if (string.IsNullOrEmpty(f.Path)) imgIndex = 0; //ROOT imageIndex
                    var rn = MainTreeView.Nodes.Add(f.Path, f.Name, imgIndex, imgIndex);
                    rn.ToolTipText = f.FullPath;
                    rn.Tag = f;
                    f.TreeNode = rn;
                }
            }
            catch { }
        }
        private void AddMainTreeViewNode(MainTreeFolder f)
        {
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => { AddMainTreeViewNode(f); }));
                }
                else
                {
                    string n = f.Name;
                    var root = f.Parent?.TreeNode;

                    RecurseAddMainTreeViewNodes(f, root);

                    root.Expand();
                }
            }
            catch { }
        }
        private void RecurseAddMainTreeViewNodes(MainTreeFolder f, TreeNode parent)
        {
            int imgIndex = 1; //FOLDER imageIndex
            if (f.RpfFile != null) imgIndex = 3; //RPF FILE imageIndex

            var nc = (parent != null) ? parent.Nodes : MainTreeView.Nodes;

            var tn = nc.Add(f.Path, f.Name, imgIndex, imgIndex);

            tn.ToolTipText = f.Path;
            tn.Tag = f;

            f.TreeNode = tn;

            if (f.Children != null)
            {
                f.Children.Sort((n1, n2) => n1.Name.CompareTo(n2.Name));

                foreach (var child in f.Children)
                {
                    RecurseAddMainTreeViewNodes(child, tn);
                }
            }
        }
        private void MainTreeViewRefreshComplete()
        {
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => { MainTreeViewRefreshComplete(); }));
                }
                else
                {
                    if (!FirstRefreshed && !string.IsNullOrEmpty(Settings.Default.RPFExplorerStartFolder))
                    {
                        Navigate(Settings.Default.RPFExplorerStartFolder);
                    }
                    else if (CurrentFolder != null)
                    {
                        if (CurrentFolder.IsSearchResults)
                        {
                            Search(CurrentFolder.SearchTerm);
                        }
                        else
                        {
                            var path = CurrentFolder.Path;
                            CurrentFolder = null;
                            Navigate(path);
                        }
                    }
                    else
                    {
                        Navigate(RootFolder);
                    }


                    if (!FirstRefreshed && Settings.Default.RPFExplorerStartInEditMode)
                    {
                        EnableEditMode(true, false);
                    }
                    FirstRefreshed = true;
                }
            }
            catch { }
        }
        private void AddNewFolderTreeNode(MainTreeFolder f)
        {
            if (CurrentFolder == null) return;

            RecurseAddMainTreeViewNodes(f, CurrentFolder.TreeNode);

            CurrentFolder.AddChild(f);

            RefreshMainListView();
        }
        private void EnsureRpfTreeNodeHierarchy(MainTreeFolder f)
        {
            //ensure this root rpf is in the tree, with all its children.
            //also create parent nodes if necessary
            //remove any existing node at this path first

            var relpath = f.Path.ToLowerInvariant();
            var hierarchy = relpath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var tf = RootFolder;
            var createdroot = (MainTreeFolder)null;
            for (int i = 0; i < hierarchy.Length - 1; i++)
            {
                var name = hierarchy[i];
                var tt = tf.FindChild(name);
                if (tt == null)//not found in hierarchy - need to create this tree folder object
                {
                    var tpath = tf.Path + "\\" + name;
                    var tfull = tf.FullPath + "\\" + name;
                    tt = CreateRootDirTreeFolder(name, tpath, tfull);
                    tf.AddChild(tt);
                    if (createdroot == null) createdroot = tt;
                }
                tf = tt;
            }

            var exchild = tf.FindChild(f.Name);
            if (exchild != null)
            {
                tf.RemoveChild(exchild);
                exchild.TreeNode?.Remove();
            }

            tf.AddChild(f);

            if (createdroot != null)
            {
                RecurseAddMainTreeViewNodes(createdroot, createdroot.Parent.TreeNode);
            }
            else
            {
                RecurseAddMainTreeViewNodes(f, tf.TreeNode);
            }

        }
        private MainTreeFolder CreateRpfTreeFolder(RpfFile rpf, string relpath, string fullpath)
        {
            var node = new MainTreeFolder();
            node.RpfFile = rpf;
            node.RpfFolder = rpf.Root;
            node.Name = rpf.Name;
            node.Path = relpath;
            node.FullPath = fullpath;
            return node;
        }
        private MainTreeFolder CreateRpfDirTreeFolder(RpfDirectoryEntry dir, string relpath, string fullpath)
        {
            var node = new MainTreeFolder();
            node.RpfFolder = dir;
            node.Name = dir.Name;
            node.Path = relpath;
            node.FullPath = fullpath;
            return node;
        }
        private MainTreeFolder CreateRootDirTreeFolder(string name, string path, string fullpath)
        {
            var node = new MainTreeFolder();
            node.Name = name;
            node.Path = path;
            node.FullPath = fullpath;
            return node;
        }
        private void RenameTreeFolder(MainTreeFolder f, string newname)
        {
            if (f.Parent == null) return;
            f.Name = newname;
            f.Path = f.Parent.Path + "\\" + newname.ToLowerInvariant();
            f.FullPath = f.Parent.FullPath + "\\" + newname;
            if (f.TreeNode != null)
            {
                f.TreeNode.Text = newname;
            }
            if (f.Children != null)
            {
                foreach (var item in f.Children)
                {
                    RenameTreeFolder(item, item.Name);//just to make sure the all the paths are correct...
                }
            }
            if (f.ListItems != null)
            {
                foreach (var item in f.ListItems)
                {
                    RenameListItem(item, item.Name);
                }
            }
        }
        private void RenameListItem(MainListItem i, string newname)
        {
            if (i.Parent == null) return;
            i.Name = newname;
            i.Path = i.Parent.Path + "\\" + newname.ToLowerInvariant();
            i.FullPath = i.Parent.FullPath + "\\" + newname;

            if (i.Parent == CurrentFolder)
            {
                int idx = CurrentFiles.IndexOf(i);
                if (idx >= 0)
                {
                    MainListView.RedrawItems(idx, idx, false);
                }
            }
        }
        private void RemoveTreeFolder(MainTreeFolder f)
        {
            if (f.Parent == null) return;

            f.Parent.Children?.Remove(f);

            if (f.TreeNode != null)
            {
                f.TreeNode.Remove();
            }
        }
        private void RemoveListItem(MainListItem i)
        {
            if (i.Parent == null) return;

            MainListView.VirtualListSize = 0;

            i.Parent.ListItems?.Remove(i);

            if (i.Parent == CurrentFolder)
            {
                CurrentFiles.Remove(i);//should really be the same list as above, but just in case...
            }

            MainListView.VirtualListSize = CurrentFiles.Count;
        }
        private void EnsureImportedRpf(RpfFileEntry entry, RpfDirectoryEntry parentrpffldr)
        {
            if ((entry == null) || (parentrpffldr == null)) return;
            var newrpf = parentrpffldr.File?.FindChildArchive(entry);
            if (newrpf == null) return;

            //an RPF file was imported. add its structure to the UI!
            var rootpath = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();
            var tnf = CreateRpfTreeFolder(newrpf, newrpf.Path, rootpath + newrpf.Path);
            CurrentFolder.AddChildToHierarchy(tnf);
            var pfolder = tnf.Parent;
            if (pfolder.Children != null)
            {
                //make sure any existing (replaced!) one is removed first!
                foreach (var child in pfolder.Children)
                {
                    if ((child != tnf) && (child.Path == tnf.Path))
                    {
                        pfolder.Children.Remove(child);
                        child.TreeNode.Remove();
                        break;
                    }
                }
            }
            RecurseMainTreeViewRPF(tnf, AllRpfs);
            RecurseAddMainTreeViewNodes(tnf, pfolder.TreeNode);
        }
        private void EnsureImportedFolder(RpfDirectoryEntry entry, RpfDirectoryEntry parentrpffldr)
        {
            if ((entry == null) || (parentrpffldr == null)) return;

            var rootpath = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();
            var tnf = CreateRpfDirTreeFolder(entry, entry.Path, rootpath + entry.Path);
            CurrentFolder.AddChildToHierarchy(tnf);

            if (tnf.Parent.TreeNode != null)
            {
                RecurseAddMainTreeViewNodes(tnf, tnf.Parent.TreeNode);
            }

            foreach (var subdir in entry.Directories)
            {
                EnsureImportedFolder(subdir, entry);
            }
            foreach (var subfile in entry.Files)
            {
                EnsureImportedRpf(subfile, entry);
            }
        }

        public void RefreshMainListViewInvoke()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => { RefreshMainListView(); }));
            }
            else
            {
                RefreshMainListView();
            }
        }
        private void RefreshMainListView()
        {
            MainListView.VirtualListSize = 0;
            if (CurrentFolder != null)
            {
                if (!CurrentFolder.IsSearchResults)
                {
                    CurrentFolder.ListItems = null; //makes sure to rebuild the current files list
                }
                CurrentFiles = CurrentFolder.GetListItems();

                foreach (var file in CurrentFiles) //cache all the data for use by the list view.
                {
                    file.CacheDetails(this);
                }

                SortMainListView(SortColumnIndex, SortDirection); //sorts CurrentItems and sets VirtualListSize

                ShowMainListViewPathColumn(CurrentFolder.IsSearchResults);
            }
            else
            {
                UpdateSelectionUI();

                ShowMainListViewPathColumn(false);
            }
        }
        private void SortMainListView(int col, SortOrder dir)
        {
            if (dir == SortOrder.None) dir = SortOrder.Ascending; //none not supported for actual sorting!

            SortColumnIndex = col;
            SortDirection = dir;

            MainListView.SetSortIcon(col, dir);

            MainListView.VirtualListSize = 0;

            if (CurrentFiles == null) return;

            CurrentFiles.Sort((i1, i2) => i1.SortCompare(i2, col, dir));

            MainListView.VirtualListSize = CurrentFiles.Count;

            UpdateSelectionUI();
        }

        private void ShowMainListViewPathColumn(bool show)
        {
            bool visible = (MainPathColumnHeader.Width > 0);
            if (show && !visible)
            {
                MainPathColumnHeader.Width = PreviousPathColumnWidth;
            }
            else if (!show && visible)
            {
                PreviousPathColumnWidth = MainPathColumnHeader.Width;
                MainPathColumnHeader.Width = 0;
                if (SortColumnIndex == 4)//path col
                {
                    SortMainListView(0, SortDirection);//switch sort to name col
                }
            }
        }


        public void Search(string text)
        {
            if (!Ready) return;
            if (Searching) return;
            if (string.IsNullOrEmpty(text)) return;

            var term = text.ToLowerInvariant();
            var terms = term.Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return;

            SearchTextBox.Text = text;
            SearchButton.Image = SearchGlobalButton.Image;
            SearchButton.Text = SearchGlobalButton.Text;
            SearchButton.Checked = false;
            SearchGlobalButton.Checked = true;
            SearchFilterButton.Checked = false;

            SearchResults = new MainTreeFolder();
            SearchResults.Name = "Search Results: " + text;
            SearchResults.Path = SearchResults.Name;
            SearchResults.IsSearchResults = true;
            SearchResults.SearchTerm = text;

            Navigate(SearchResults);

            SortDirection = SortOrder.None;
            MainListView.SetSortIcon(SortColumnIndex, SortDirection);
            MainListView.VirtualListSize = 0;

            CurrentFiles.Clear();

            UpdateStatus("Searching...");

            //Task.Run(() =>
            //{
                Searching = true;

            Cursor = Cursors.WaitCursor;

                var resultcount = RootFolder.Search(terms, this);

                if (Searching)
                {
                    Searching = false;
                    UpdateStatus("Search complete. " + resultcount.ToString() + " items found.");
                }
                else
                {
                    UpdateStatus("Search aborted. " + resultcount.ToString() + " items found.");
                }

            Cursor = Cursors.Default;

            //});
        }

        public void Filter(string text)
        {
            SearchGlobalButton.Checked = false;
            SearchFilterButton.Checked = !SearchFilterButton.Checked;
            SearchTextBox.Text = text;
            SearchButton.Image = SearchFilterButton.Image;
            SearchButton.Text = SearchFilterButton.Text;
            SearchButton.Checked = SearchFilterButton.Checked;

            FilterUpdate();
        }
        private void FilterUpdate()
        {
            var term = SearchTextBox.Text;
            var terms = term.Split(new[] { '*' }, StringSplitOptions.RemoveEmptyEntries);
            bool filterenabled = SearchFilterButton.Checked;
            if (filterenabled && (terms.Length > 0) && (!string.IsNullOrEmpty(term)))
            {
                var origfiles = CurrentFolder.GetListItems();
                var filtered = new List<MainListItem>();
                foreach (var file in origfiles)
                {
                    if (MainTreeFolder.SearchMatch(file.Name.ToLowerInvariant(), terms))
                    {
                        filtered.Add(file);
                    }
                }
                CurrentFiles = filtered;
            }
            else
            {
                CurrentFiles = CurrentFolder.GetListItems();
            }
            SortMainListView(SortColumnIndex, SortDirection); //sorts CurrentItems and sets VirtualListSize
        }


        public void AddSearchResult(MainListItem item)
        {
            if (SearchResults == null) return;
            if (SearchResults.ListItems != CurrentFiles) return;

            if (item != null)
            {
                item.CacheDetails(this);
                CurrentFiles.Add(item);
            }
            else
            {
                MainListView.VirtualListSize = CurrentFiles.Count;
            }

            //try
            //{
            //    if (InvokeRequired)
            //    {
            //        BeginInvoke(new Action(() => { AddSearchResultInner(item); }));
            //    }
            //    else
            //    {
            //        AddSearchResultInner(item);
            //    }
            //}
            //catch { }
        }




        private byte[] GetFileData(MainListItem file)
        {
            byte[] data = null;
            if (file.Folder != null)
            {
                var entry = file.Folder.RpfFile?.ParentFileEntry;
                if (entry != null)
                {
                    data = entry.File.ExtractFile(entry);//extract an RPF from another.
                }
                else if (!string.IsNullOrEmpty(file.FullPath) && (file.Folder.RpfFile != null))
                {
                    data = File.ReadAllBytes(file.FullPath); //load RPF file from filesystem
                }
            }
            else if (file.File != null) //load file from RPF
            {
                if (file.File.File != null) //need the reference to the RPF archive
                {
                    data = file.File.File.ExtractFile(file.File);
                }
                else
                { }
            }
            else if (!string.IsNullOrEmpty(file.FullPath))
            {
                data = File.ReadAllBytes(file.FullPath); //load file from filesystem
            }
            else
            { }
            return data;
        }
        private byte[] GetFileDataCompressResources(MainListItem file)
        {
            byte[] data = GetFileData(file);
            RpfResourceFileEntry rrfe = file.File as RpfResourceFileEntry;
            if (rrfe != null) //add resource header if this is a resource file.
            {
                data = ResourceBuilder.Compress(data); //not completely ideal to recompress it...
                data = ResourceBuilder.AddResourceHeader(rrfe, data);
            }
            return data;
        }
        private string GetFileXml(MainListItem file, out string newfn, string outputFolder, Action<string> errorAction)
        {
            newfn = null;
            if (!CanExportXml(file)) return null;

            var data = GetFileData(file);
            if (data == null)
            {
                errorAction("Unable to extract file: " + file.Path);
                return null;
            }

            var fentry = file?.File;
            if (fentry is Rpf9FileEntry f9)
            {
                //GTA VI: PSIN / PRD0 metadata, or a plain text XML file
                if (!Rpf9Psin.IsMetadata(data, f9) && IsTextXml(data))
                {
                    newfn = f9.Name;
                    return Encoding.UTF8.GetString(data).TrimStart('﻿');
                }
                string x9 = null;
                try { x9 = Rpf9Psin.ToXml(data, f9); }
                catch (Exception ex) { errorAction("Unable to convert file to XML: " + file.Path + " (" + ex.Message + ")"); return null; }
                if (x9 == null) { errorAction("Not GTA VI metadata (PSIN/PSO/RBF0/PRD0): " + file.Path); return null; }
                newfn = f9.Name + ".xml";
                return x9;
            }
            if (fentry == null)
            {
                //this should only happen when opening a file from filesystem...
                var name = new FileInfo(file.FullPath).Name;
                fentry = CreateFileEntry(name, file.FullPath, ref data);
            }

            var xml = MetaXml.GetXml(fentry, data, out newfn, outputFolder);
            if (string.IsNullOrEmpty(xml))
            {
                errorAction("Unable to convert file to XML: " + file.Path);
                return null;
            }
            return xml;
        }


        private bool CanViewFile(MainListItem item)
        {
            if (item == null) return false;
            if (item.FileType == null) return false;
            switch (item.FileType.DefaultAction)
            {
                case FileTypeAction.ViewText:
                case FileTypeAction.ViewXml:
                case FileTypeAction.ViewYtd:
                case FileTypeAction.ViewYmt:
                case FileTypeAction.ViewYmf:
                case FileTypeAction.ViewYmap:
                case FileTypeAction.ViewYtyp:
                case FileTypeAction.ViewJPso:
                case FileTypeAction.ViewModel:
                case FileTypeAction.ViewCut:
                case FileTypeAction.ViewAwc:
                case FileTypeAction.ViewGxt:
                case FileTypeAction.ViewRel:
                case FileTypeAction.ViewFxc:
                case FileTypeAction.ViewYwr:
                case FileTypeAction.ViewYvr:
                case FileTypeAction.ViewYcd:
                case FileTypeAction.ViewYnd:
                case FileTypeAction.ViewCacheDat:
                case FileTypeAction.ViewYed:
                case FileTypeAction.ViewYld:
                case FileTypeAction.ViewYfd:
                case FileTypeAction.ViewHeightmap:
                case FileTypeAction.ViewMrf:
                case FileTypeAction.ViewDistantLights:
                    return true;
                case FileTypeAction.ViewHex:
                default:
                    break;
            }
            return false;
        }

        private bool CanExportXml(MainListItem item)
        {
            if (item == null) return false;
            if (item.FileType == null) return false;
            return item.FileType.XmlConvertible;
        }


        private void View(MainListItem item)
        {
#if !DEBUG
            try
#endif
            {
                var exform = FindExistingForm(item?.File);
                if (exform != null)
                {
                    exform.Focus();
                    return;
                }

                if (item.File == null && (item.FullPath?.EndsWith(".ttpf", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    ViewTtdb(item.FullPath); //GTA VI texture packs are GBs: list them without reading the whole file
                    return;
                }

                byte[] data = null;
                string name = "";
                string path = "";
                if (item.File != null)
                {
                    //load file from RPF
                    if (item.File.File == null) return; //no RPF file? go no further
                    data = item.File.File.ExtractFile(item.File);
                    name = item.Name;
                    path = item.FullPath;
                }
                else if (!string.IsNullOrEmpty(item.FullPath))
                {
                    //load file from filesystem
                    data = File.ReadAllBytes(item.FullPath);
                    name = new FileInfo(item.FullPath).Name;
                    path = item.FullPath;
                }

                if (data == null) return;

                var ft = item.FileType;
                var fe = item.File;
                if (fe == null)
                { 
                    //this should only happen when opening a file from filesystem...
                    fe = CreateFileEntry(name, path, ref data);
                }

                if (fe is Rpf9FileEntry fe9)
                {
                    if ((fe9.NameLower ?? "").EndsWith(".zkit") && ViewGta6Kit(name, path, data, fe9)) return;
                    if (ViewRpf9Meta(name, path, data, fe9)) return;
                    if (ViewRpf9SecMotion(name, path, data, fe9)) return;
                    if (ft.DefaultAction == FileTypeAction.ViewXml && !IsTextXml(data)) { ViewHex(name, path, data); return; } //binary metadata that didn't decode
                }

                switch (ft.DefaultAction)
                {
                    case FileTypeAction.ViewText:
                        ViewText(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewXml:
                        ViewXml(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYtd:
                        ViewYtd(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYmt:
                        ViewYmt(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYmf:
                        ViewYmf(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYmap:
                        ViewYmap(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYtyp:
                        ViewYtyp(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewJPso:
                        ViewJPso(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewCut:
                        ViewCut(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewModel:
                        ViewModel(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewAwc:
                        ViewAwc(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewGxt:
                        ViewGxt(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewRel:
                        ViewRel(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewFxc:
                        ViewFxc(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYwr:
                        ViewYwr(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYvr:
                        ViewYvr(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYcd:
                        ViewYcd(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYnd:
                        ViewYnd(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewCacheDat:
                        ViewCacheDat(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYed:
                        ViewYed(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYld:
                        ViewYld(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYfd:
                        ViewYfd(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewHeightmap:
                        ViewHeightmap(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewMrf:
                        ViewMrf(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewNametable:
                        ViewNametable(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewDistantLights:
                        ViewDistantLights(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewYpdb:
                        ViewYpdb(name, path, data, fe);
                        break;
                    case FileTypeAction.ViewHex:
                    default:
                        ViewHex(name, path, data);
                        break;
                }
            }
#if !DEBUG
            catch (Exception ex)
            {
                UpdateErrorLog(ex.ToString());
                return;
            }
#endif
        }
        private void ViewHex(MainListItem item)
        {
            try
            {
                byte[] data = null;
                string name = "";
                string path = "";
                if (item.File != null)
                {
                    //load file from RPF
                    if (item.File.File == null) return; //no RPF file? go no further
                    data = item.File.File.ExtractFile(item.File);
                    name = item.Name;
                    path = item.FullPath;
                }
                else if (!string.IsNullOrEmpty(item.FullPath))
                {
                    //load file from filesystem
                    data = File.ReadAllBytes(item.FullPath);
                    name = new FileInfo(item.FullPath).Name;
                    path = item.FullPath;
                }

                if (data == null) return;

                ViewHex(name, path, data);
            }
            catch (Exception ex)
            {
                UpdateErrorLog(ex.ToString());
                return;
            }
        }
        private void ViewHex(string name, string path, byte[] data)
        {
            HexForm f = new HexForm();
            f.Show();
            f.LoadData(name, path, data);
        }
        /// <summary>true for text XML (optional UTF-8 BOM and leading whitespace, then '&lt;'), as opposed to binary metadata</summary>
        private static bool IsTextXml(byte[] data)
        {
            if (data == null) return false;
            int i = (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) ? 3 : 0;
            while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\r' || data[i] == '\n')) i++;
            return i < data.Length && data[i] == '<';
        }
        /// <summary>GTA VI PSIN / PRD0 metadata (any extension) -> decoded XML in the XML viewer. false if it isn't metadata.</summary>
        private bool ViewRpf9Meta(string name, string path, byte[] data, Rpf9FileEntry e)
        {
            if (!Rpf9Psin.IsMetadata(data, e)) return false;
            string xml;
            try
            {
                Cursor = Cursors.WaitCursor;
                xml = Rpf9Psin.ToXml(data, e);
            }
            catch (Exception ex)
            {
                UpdateStatus(name + ": metadata decode failed (" + ex.GetType().Name + ": " + ex.Message + "), showing hex");
                return false;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            if (xml == null) return false;
            XmlForm f = new XmlForm(this);
            f.Show();
            f.LoadXml(name + ".xml", path + ".xml", xml, null); //no RPF entry: saving goes to a file, never back into the archive
            return true;
        }
        private void ViewXml(string name, string path, byte[] data, RpfFileEntry e)
        {
            string xml = Encoding.UTF8.GetString(data).TrimStart('﻿'); //drop the UTF-8 BOM (EF BB BF)
            XmlForm f = new XmlForm(this);
            f.Show();
            f.LoadXml(name, path, xml, e);
        }
        private void ViewText(string name, string path, byte[] data, RpfFileEntry e)
        {
            string txt = Encoding.UTF8.GetString(data);
            TextForm f = new TextForm(this);
            f.Show();
            f.LoadText(name, path, txt, e);
        }
        private void ViewTtdb(string path)
        {
            var name = Path.GetFileName(path);
            Rpf9Ttdb pack;
            try { pack = new Rpf9Ttdb(path); }
            catch (Exception ex) { MessageBox.Show("Unable to open texture pack: " + ex.Message, name); return; }
            var list = new List<Texture>();
            foreach (var k in pack.Index.Keys) list.Add(pack.Placeholder(k));
            var td = new TextureDictionary();
            td.BuildFromTextureList(list);
            var f = new YtdForm(this);
            f.TextureLoader = t => pack.Load(t);
            f.FormClosed += (s, a) => pack.Dispose();
            f.Show();
            f.LoadYtd(new YtdFile { Name = name, TextureDict = td });
            UpdateStatus(name + ": " + list.Count + " textures (named by TTDB key; decoded when selected)");
        }
        private void ViewYtd(string name, string path, byte[] data, RpfFileEntry e)
        {
            if (e is Rpf9FileEntry e9)
            {
                //GTA VI texture dictionary
                var notes = new List<string>();
                var td = Rpf9Texture.ReadDictionary(data, e9.VirtualSize, notes);
                if (td == null)
                {
                    MessageBox.Show("Not a texture dictionary this version understands.", name);
                    ViewHex(name, path, data);
                    return;
                }
                var ytd9 = new YtdFile { Name = name, RpfFileEntry = e, TextureDict = td };
                YtdForm f9 = new YtdForm(this);
                f9.Show();
                f9.LoadYtd(ytd9);
                if (notes.Count > 0) UpdateStatus(name + ": " + notes.Count + " texture(s) skipped: " + string.Join("; ", notes.Take(5)));
                return;
            }
            var ytd = RpfFile.GetFile<YtdFile>(e, data);
            YtdForm f = new YtdForm(this);
            f.Show();
            f.LoadYtd(ytd);
        }
        private void ViewYmt(string name, string path, byte[] data, RpfFileEntry e)
        {
            var ymt = RpfFile.GetFile<YmtFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(ymt);
        }
        private void ViewYmf(string name, string path, byte[] data, RpfFileEntry e)
        {
            var ymf = RpfFile.GetFile<YmfFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(ymf);
        }
        private void ViewYmap(string name, string path, byte[] data, RpfFileEntry e)
        {
            var ymap = RpfFile.GetFile<YmapFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(ymap);
        }
        private void ViewYtyp(string name, string path, byte[] data, RpfFileEntry e)
        {
            var ytyp = RpfFile.GetFile<YtypFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(ytyp);
        }
        private void ViewJPso(string name, string path, byte[] data, RpfFileEntry e)
        {
            var pso = RpfFile.GetFile<JPsoFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(pso);
        }
        private Rpf9TextureStore gta6Textures;
        /// <summary>GTA VI streamed textures: every *.ttpf under the game folder (opened on first use).</summary>
        private Rpf9TextureStore Gta6Textures
        {
            get
            {
                if (gta6Textures == null)
                {
                    var folder = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
                    gta6Textures = new Rpf9TextureStore(folder);
                }
                return gta6Textures;
            }
        }

        /// <summary>GTA VI entries the viewers look up by name: clip dictionaries, secondary motion and its asset maps, drawables.</summary>
        private class Gta6EntryIndex
        {
            public readonly Dictionary<string, Rpf9FileEntry> ClipDicts = new Dictionary<string, Rpf9FileEntry>(); // *.zcd by lowercase name without extension
            public readonly Dictionary<ulong, Rpf9FileEntry> ByHash = new Dictionary<ulong, Rpf9FileEntry>();      // .zsm/.zdr/.zdd/.zft by RPF9 name hash
            public readonly List<Rpf9FileEntry> AssetMaps = new List<Rpf9FileEntry>();                              // *.zsmam
            public readonly Dictionary<string, Rpf9FileEntry> Fragments = new Dictionary<string, Rpf9FileEntry>(); // *.zft by lowercase name without extension (character rigs)
            public readonly Dictionary<uint, Rpf9FileEntry> FragmentsByJoaat = new Dictionary<uint, Rpf9FileEntry>(); // *.zft by joaat(lowercase name without extension) (kit "fragment" tags)
            public Dictionary<ulong, ulong[]> SecMotion;          // drawable name hash -> .zsm name hashes (all asset maps merged)
            public Dictionary<ulong, List<ulong>> SecMotionUsers; // .zsm name hash -> drawable name hashes
            public List<string> SecMotionErrors = new List<string>();
        }
        private Gta6EntryIndex gta6Index;
        private Gta6EntryIndex Gta6Index
        {
            get
            {
                if (gta6Index != null) return gta6Index;
                var res = new Gta6EntryIndex();
                var rpfs = AllRpfs;
                if (rpfs == null) return res; // not loaded yet, try again next time
                var seen = new HashSet<RpfFile>();
                var stack = new Stack<RpfFile>(rpfs);
                while (stack.Count > 0)
                {
                    var rpf = stack.Pop();
                    if (rpf == null || !seen.Add(rpf)) continue;
                    if (rpf.Children != null) foreach (var c in rpf.Children) stack.Push(c);
                    if (rpf.AllEntries == null) continue;
                    foreach (var fe in rpf.AllEntries.OfType<Rpf9FileEntry>())
                    {
                        var nl = fe.NameLower;
                        if (nl.EndsWith(".zcd"))
                        {
                            var key = nl.Substring(0, nl.Length - 4);
                            if (!res.ClipDicts.ContainsKey(key)) res.ClipDicts[key] = fe;
                        }
                        else if (nl.EndsWith(".zsmam")) res.AssetMaps.Add(fe);
                        else if (nl.EndsWith(".zsm") || nl.EndsWith(".zdr") || nl.EndsWith(".zdd") || nl.EndsWith(".zft"))
                        {
                            if (fe.NameHash64 != 0 && !res.ByHash.ContainsKey(fe.NameHash64)) res.ByHash[fe.NameHash64] = fe;
                            if (nl.EndsWith(".zft"))
                            {
                                var key = nl.Substring(0, nl.Length - 4);
                                if (!res.Fragments.ContainsKey(key)) res.Fragments[key] = fe;
                                var jh = Rpf9Psin.Joaat(key);
                                if (!res.FragmentsByJoaat.ContainsKey(jh)) res.FragmentsByJoaat[jh] = fe;
                            }
                        }
                    }
                }
                gta6Index = res;
                return res;
            }
        }
        /// <summary>GTA VI secondary motion asset maps (*.zsmam), merged and decoded on first use.</summary>
        private Gta6EntryIndex Gta6SecMotionIndex
        {
            get
            {
                var ix = Gta6Index;
                if (ix.SecMotion != null || ix.AssetMaps.Count == 0) return ix;
                var map = new Dictionary<ulong, ulong[]>();
                var users = new Dictionary<ulong, List<ulong>>();
                foreach (var fe in ix.AssetMaps)
                {
                    try
                    {
                        var data = fe.File.ExtractFile(fe);
                        if (data == null) { ix.SecMotionErrors.Add("unable to extract " + fe.Path); continue; }
                        foreach (var kv in Rpf9SecMotion.ReadAssetMap(data))
                        {
                            map[kv.Key] = map.TryGetValue(kv.Key, out var old) ? old.Concat(kv.Value).Distinct().ToArray() : kv.Value;
                            foreach (var z in kv.Value)
                            {
                                if (!users.TryGetValue(z, out var l)) users[z] = l = new List<ulong>();
                                if (!l.Contains(kv.Key)) l.Add(kv.Key);
                            }
                        }
                    }
                    catch (Exception ex) { ix.SecMotionErrors.Add(fe.Path + ": " + ex.Message); }
                }
                ix.SecMotion = map;
                ix.SecMotionUsers = users;
                return ix;
            }
        }
        private readonly List<KeyValuePair<string, Rpf9Anim.Dict>> gta6ClipCache = new List<KeyValuePair<string, Rpf9Anim.Dict>>();
        /// <summary>GTA VI clip dictionaries (*.zcd) in the loaded archives, by lowercase name without extension.</summary>
        private Dictionary<string, Rpf9FileEntry> Gta6ClipDicts => Gta6Index.ClipDicts;

        /// <summary>Secondary motion (.zsm) linked to a GTA VI drawable through the loaded .zsmam asset maps.</summary>
        private List<KeyValuePair<string, Rpf9SecMotion.Zsm>> LoadGta6SecMotion(Rpf9FileEntry drawable, List<string> notes)
        {
            var res = new List<KeyValuePair<string, Rpf9SecMotion.Zsm>>();
            var ix = Gta6SecMotionIndex;
            if (ix.SecMotion == null || drawable == null) return res;
            if (!ix.SecMotion.TryGetValue(drawable.NameHash64, out var list)) return res;
            foreach (var h in list)
            {
                if (!ix.ByHash.TryGetValue(h, out var fe)) { notes.Add(h.ToString("x16") + ".zsm not in the loaded archives"); continue; }
                try
                {
                    var data = fe.File.ExtractFile(fe);
                    if (data == null) { notes.Add("unable to extract " + fe.Name); continue; }
                    res.Add(new KeyValuePair<string, Rpf9SecMotion.Zsm>(fe.Name, Rpf9SecMotion.Read(data)));
                }
                catch (Exception ex) { notes.Add(fe.Name + ": " + ex.Message); }
            }
            return res;
        }

        /// <summary>GTA VI .zsm / .zsmam: text view. Returns false for other files.</summary>
        private bool ViewRpf9SecMotion(string name, string path, byte[] data, Rpf9FileEntry e)
        {
            var nl = e.NameLower ?? "";
            bool zsm = nl.EndsWith(".zsm"), zsmam = nl.EndsWith(".zsmam");
            if (!zsm && !zsmam) return false;
            string txt;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (zsm)
                {
                    var z = Rpf9SecMotion.Read(data);
                    var ix = Gta6SecMotionIndex;
                    var usedBy = new List<string>();
                    if (ix.SecMotionUsers != null && ix.SecMotionUsers.TryGetValue(e.NameHash64, out var users))
                        foreach (var u in users) usedBy.Add(ix.ByHash.TryGetValue(u, out var de) ? de.Path : u.ToString("x16"));
                    txt = Rpf9SecMotion.Summary(z, null, usedBy);
                    if (z.Type == 2 || z.Type == 1) txt = "Bone tags show as hashes here. Open one of the drawables under \"used by\" to see bone names and this motion on its skeleton.\r\n\r\n" + txt;
                }
                else
                {
                    var map = Rpf9SecMotion.ReadAssetMap(data);
                    var ix = Gta6Index;
                    string N(ulong h) => ix.ByHash.TryGetValue(h, out var fe) ? fe.Path : h.ToString("x16");
                    var sb = new StringBuilder();
                    sb.AppendLine("Secondary motion asset map: " + map.Count + " drawables");
                    sb.AppendLine();
                    foreach (var kv in map.OrderBy(kv => N(kv.Key), StringComparer.OrdinalIgnoreCase))
                    {
                        sb.AppendLine(N(kv.Key));
                        foreach (var z in kv.Value) sb.AppendLine("    " + N(z));
                    }
                    txt = sb.ToString();
                }
            }
            catch (Exception ex) { txt = "Not a file this version understands: " + ex.Message; }
            finally { Cursor = Cursors.Default; }
            TextForm tf = new TextForm(this);
            tf.Show();
            tf.LoadReadOnlyText(name + ".txt", path, txt.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            return true;
        }
        /// <summary>ModelForm callback: GTA VI clip dictionary bound to the viewed skeleton (decoded dictionaries are cached).</summary>
        private YcdFile LoadGta6ClipDict(string name, Skeleton skel, out string note)
        {
            note = null;
            if (!Gta6ClipDicts.TryGetValue(name, out var fe)) return null;
            Rpf9Anim.Dict dict = null;
            int ci = gta6ClipCache.FindIndex(kv => kv.Key == name);
            if (ci >= 0)
            {
                dict = gta6ClipCache[ci].Value;
                gta6ClipCache.RemoveAt(ci);
            }
            else
            {
                var data = fe.File.ExtractFile(fe);
                if (data == null) { note = "unable to extract " + fe.Path; return null; }
                dict = Rpf9Anim.Read(data, fe.VirtualSize);
            }
            gta6ClipCache.Add(new KeyValuePair<string, Rpf9Anim.Dict>(name, dict));
            if (gta6ClipCache.Count > 16) gta6ClipCache.RemoveAt(0);
            var ycd = Rpf9Anim.ToYcd(dict, name, Rpf9Drawable.BoneHashMap(skel), out int matched, out int total);
            note = name + ": " + ycd.ClipMapEntries.Length + " clips, " + matched + "/" + total + " bone tracks match this skeleton"
                + (dict.Failed > 0 ? ", " + dict.Failed + " animations failed to decode" : "");
            return ycd;
        }

        /// <summary>
        /// GTA VI: the rig skeleton of a metaped part, from the .zft whose name is the longest prefix of the part's
        /// name at an underscore (crm_dog_poodlestandard_m_00_fur_head_00 -> crm_dog_poodlestandard_m_00.zft).
        /// </summary>
        private Skeleton LoadGta6Rig(Rpf9FileEntry part, out string rigName)
        {
            rigName = null;
            var nl = part?.NameLower ?? "";
            var stem = Path.GetFileNameWithoutExtension(nl);
            var frags = Gta6Index.Fragments;
            for (int i = stem.LastIndexOf('_'); i > 0; i = stem.LastIndexOf('_', i - 1))
            {
                if (!frags.TryGetValue(stem.Substring(0, i), out var fe) || fe == part) continue;
                var sk = ReadGta6Rig(fe);
                if (sk == null) continue;
                rigName = fe.Name;
                return sk;
            }
            return null;
        }

        private Skeleton ReadGta6Rig(Rpf9FileEntry fe)
        {
            try
            {
                var data = fe?.File?.ExtractFile(fe);
                return data == null ? null : Rpf9Drawable.ReadSkeletonOnly(data, fe.VirtualSize);
            }
            catch (Exception) { return null; }
        }

        private class Gta6RigInfo { public Rpf9FileEntry Entry; public HashSet<uint> Bones; public int Count; }
        private List<Gta6RigInfo> gta6Rigs;
        /// <summary>The bone name hashes of every .zft skeleton in the loaded archives (read once, about 2 s for packs.rpf).</summary>
        private List<Gta6RigInfo> Gta6Rigs
        {
            get
            {
                if (gta6Rigs != null) return gta6Rigs;
                var frags = Gta6Index.Fragments;
                var list = new List<Gta6RigInfo>();
                foreach (var fe in frags.Values)
                {
                    var sk = ReadGta6Rig(fe);
                    var hm = Rpf9Drawable.BoneHashMap(sk);
                    if (hm == null || sk.Bones.Items.Length < 20) continue;
                    list.Add(new Gta6RigInfo { Entry = fe, Bones = new HashSet<uint>(hm.Keys), Count = sk.Bones.Items.Length });
                }
                if (frags.Count > 0) gta6Rigs = list;
                return list;
            }
        }

        /// <summary>
        /// GTA VI: the rig for meshes that use these bones, when no name says which (hash-named parts, human kits name no
        /// fragment): the .zft skeleton that has most of them, the smallest one of those. Null unless it has at least half.
        /// </summary>
        private Skeleton FindGta6RigByBones(HashSet<uint> used, Rpf9FileEntry exclude, out string rigName)
        {
            rigName = null;
            if (used == null || used.Count == 0) return null;
            Gta6RigInfo best = null; int bestHits = 0;
            foreach (var r in Gta6Rigs)
            {
                if (r.Entry == exclude) continue;
                int hits = 0;
                foreach (var h in used) if (r.Bones.Contains(h)) hits++;
                if (hits > bestHits || (hits == bestHits && best != null && r.Count < best.Count)) { best = r; bestHits = hits; }
            }
            if (best == null || bestHits * 2 < used.Count) return null;
            var sk = ReadGta6Rig(best.Entry);
            if (sk != null) rigName = best.Entry.Name + " (best bone match, " + bestHits + "/" + used.Count + ")";
            return sk;
        }

        private readonly Dictionary<uint, Rpf9FileEntry> gta6BoneDonor = new Dictionary<uint, Rpf9FileEntry>(); // bone name hash -> a .zdd/.zft whose skeleton has it
        private readonly HashSet<uint> gta6BoneNoDonor = new HashSet<uint>();                                    // searched everywhere, not found
        private readonly HashSet<Rpf9FileEntry> gta6BoneScanned = new HashSet<Rpf9FileEntry>();

        /// <summary>
        /// GTA VI: skeletons that have these bones (garment layer bones a part uses but another part carries, GM_L01_*).
        /// Reads skeletons of the parts' own archives first, then the rest, until every bone is found or about 20 s pass;
        /// remembers what it found for later kits.
        /// </summary>
        private IList<Skeleton> FindGta6BoneDonors(HashSet<uint> miss, IList<Rpf9FileEntry> near)
        {
            var res = new List<Skeleton>();
            var left = new HashSet<uint>(miss.Where(h => !gta6BoneNoDonor.Contains(h)));
            var useEntries = new List<Rpf9FileEntry>();
            foreach (var h in left.ToList())
                if (gta6BoneDonor.TryGetValue(h, out var de)) { if (!useEntries.Contains(de)) useEntries.Add(de); }
            void Take(Rpf9FileEntry fe, Skeleton sk)
            {
                var hm = Rpf9Drawable.BoneHashMap(sk);
                if (hm == null) return;
                bool any = false;
                foreach (var h in hm.Keys)
                {
                    if (!gta6BoneDonor.ContainsKey(h)) gta6BoneDonor[h] = fe;
                    if (left.Remove(h)) any = true;
                }
                if (any) res.Add(sk);
            }
            foreach (var de in useEntries) Take(de, ReadGta6Rig(de));
            if (left.Count == 0) return res;

            var ix = Gta6Index;
            var nearFiles = new HashSet<RpfFile>(near.Where(x => x?.File != null).Select(x => x.File));
            var cands = ix.ByHash.Values.Where(x => nearFiles.Contains(x.File))
                .Concat(ix.ByHash.Values.Where(x => !nearFiles.Contains(x.File)))
                .Where(x => x.NameLower.EndsWith(".zdd") || x.NameLower.EndsWith(".zft"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool complete = true;
            foreach (var fe in cands)
            {
                if (left.Count == 0) break;
                if (sw.ElapsedMilliseconds > 20000) { complete = false; break; }
                if (!gta6BoneScanned.Add(fe)) continue;
                var sk = ReadGta6Rig(fe);
                if (sk == null) continue;
                Take(fe, sk);
            }
            if (complete) gta6BoneNoDonor.UnionWith(left);
            return res;
        }

        /// <summary>GTA VI .zdd with no rig by name: the rig by the bones its meshes use that its own skeleton lacks.</summary>
        private Skeleton FindGta6RigForPart(byte[] data, Rpf9FileEntry part, out string rigName)
        {
            rigName = null;
            if (!(part?.NameLower ?? "").EndsWith(".zdd")) return null;
            try
            {
                var used = Rpf9Drawable.UsedBoneHashes(data, part.VirtualSize);
                var own = Rpf9Drawable.BoneHashMap(Rpf9Drawable.ReadSkeletonOnly(data, part.VirtualSize));
                if (own != null && used.All(h => own.ContainsKey(h))) return null;
                return FindGta6RigByBones(used, part, out rigName);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// GTA VI character kit (.zkit): pick an outfit and view all its parts as one model on one skeleton. Returns false
        /// to show the kit as XML instead (asked for, or nothing to show).
        /// </summary>
        private bool ViewGta6Kit(string name, string path, byte[] data, Rpf9FileEntry e9)
        {
            Rpf9Kit kit;
            try
            {
                Cursor = Cursors.WaitCursor;
                kit = Rpf9Kit.Read(data, e9);
            }
            catch (Exception ex)
            {
                UpdateStatus(name + ": kit decode failed (" + ex.Message + "), showing it as metadata");
                return false;
            }
            finally { Cursor = Cursors.Default; }
            if (kit == null) return false;
            var ix = Gta6Index;
            string PartName(Rpf9Kit.Part p) => ix.ByHash.TryGetValue(p.Drawable, out var pe) ? Path.GetFileNameWithoutExtension(pe.Name) : p.NameText;
            var outfits = new List<Rpf9Kit.Outfit>();
            var labels = new List<string>();
            foreach (var o in kit.Outfits)
            {
                var ps = kit.Resolve(o, out _);
                int found = ps.Count(p => ix.ByHash.ContainsKey(p.Drawable));
                if (found == 0) continue;
                outfits.Add(o);
                labels.Add(o.NameText + "  (" + found + " parts: " + string.Join(", ", ps.Where(p => ix.ByHash.ContainsKey(p.Drawable)).Select(PartName)) + ")");
            }
            if (outfits.Count == 0) { UpdateStatus(name + ": no outfit with parts in the loaded archives, showing the kit as metadata"); return false; }

            int pick = PickGta6Outfit(name, labels);
            if (pick == -1) return false;   // XML
            if (pick < 0) return true;      // cancelled
            var outfit = outfits[pick];
            var parts = kit.Resolve(outfit, out uint frag);
            var notes = new List<string>();
            Rpf9Drawable.Item item = null;
            string rigNote = "";
            var partEntries = new List<Rpf9FileEntry>();
            Cursor = Cursors.WaitCursor;
            try
            {
                var kps = new List<Rpf9Drawable.KitPart>();
                foreach (var p in parts)
                {
                    if (!ix.ByHash.TryGetValue(p.Drawable, out var pe)) { notes.Add(p.NameText + " not in the loaded archives"); continue; }
                    var pd = pe.File.ExtractFile(pe);
                    if (pd == null) { notes.Add("unable to extract " + pe.Name); continue; }
                    kps.Add(new Rpf9Drawable.KitPart { Name = pe.Name, Data = pd, VirtualSize = pe.VirtualSize, TextureSwaps = kit.TextureSwaps(p) });
                    partEntries.Add(pe);
                }
                Skeleton rig = null; string rigName = null;
                if (frag != 0 && ix.FragmentsByJoaat.TryGetValue(frag, out var fe))
                {
                    rig = ReadGta6Rig(fe);
                    if (rig != null) rigName = fe.Name;
                }
                if (rig == null)
                {
                    var used = new HashSet<uint>();
                    foreach (var kp in kps) { try { used.UnionWith(Rpf9Drawable.UsedBoneHashes(kp.Data, kp.VirtualSize)); } catch (Exception) { } }
                    rig = FindGta6RigByBones(used, null, out rigName);
                }
                item = Rpf9Drawable.ReadKit(Path.GetFileNameWithoutExtension(name) + " " + outfit.NameText, kps, rig, notes,
                    miss => FindGta6BoneDonors(miss, partEntries));
                rigNote = rig != null ? ", on rig " + rigName : ", no rig found";
                if (item != null && item.MissingBones > 0) rigNote += " (" + item.MissingBones + " bones the meshes name aren't in the skeleton)";
            }
            finally { Cursor = Cursors.Default; }
            if (item == null)
            {
                MessageBox.Show("None of the outfit's parts decoded." + (notes.Count > 0 ? "\n" + string.Join("\n", notes.Take(5)) : ""), name);
                return true;
            }
            ShowGta6Model(name, e9, new List<Rpf9Drawable.Item> { item }, notes, ", outfit " + outfit.NameText + " (" + partEntries.Count + " parts)" + rigNote, partEntries);
            return true;
        }

        /// <summary>Outfit chooser: index into labels, -1 = show as XML, -2 = cancelled.</summary>
        private int PickGta6Outfit(string title, List<string> labels)
        {
            using (var f = new Form())
            {
                f.Text = title + ": pick an outfit";
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new System.Drawing.Size(900, 420);
                f.MinimizeBox = false; f.MaximizeBox = false; f.ShowIcon = false; f.ShowInTaskbar = false;
                var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
                foreach (var l in labels) list.Items.Add(l);
                list.SelectedIndex = 0;
                var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 36, Padding = new Padding(4) };
                var ok = new Button { Text = "View model", DialogResult = DialogResult.OK, AutoSize = true };
                var xml = new Button { Text = "Show XML", DialogResult = DialogResult.Retry, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                panel.Controls.Add(cancel); panel.Controls.Add(xml); panel.Controls.Add(ok);
                f.Controls.Add(list); f.Controls.Add(panel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                list.DoubleClick += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
                var r = f.ShowDialog(this);
                if (r == DialogResult.Retry) return -1;
                if (r != DialogResult.OK || list.SelectedIndex < 0) return -2;
                return list.SelectedIndex;
            }
        }

        /// <summary>GTA VI drawables in the model viewer: textures, clip dictionaries, secondary motion of the source entries, status line.</summary>
        private void ShowGta6Model(string name, RpfFileEntry e, List<Rpf9Drawable.Item> items, List<string> notes, string rigNote, List<Rpf9FileEntry> secMotionSources)
        {
            int textured = 0, texShaders = 0;
            string texNote = "";
            Cursor = Cursors.WaitCursor;
            try
            {
                var store = Gta6Textures;
                if (store != null && store.KeyCount > 0)
                {
                    int errs = store.Errors.Count;
                    textured = store.Apply(items);
                    if (store.Errors.Count > errs) texNote = "; texture errors: " + string.Join("; ", store.Errors.Skip(errs).Take(2));
                }
                else texNote = "; no TTDB texture packs (*.ttpf) found";
            }
            catch (Exception ex) { texNote = "; textures failed: " + ex.Message; }
            finally { Cursor = Cursors.Default; }
            texShaders = items.Sum(i => i.TexRefs.Select(t => t.Shader).Distinct().Count());
            var gf = Rpf9Drawable.ToGameFile(name, e, items);
            if (gf == null)
            {
                MessageBox.Show("No drawable this version understands." + (notes.Count > 0 ? "\n" + string.Join("\n", notes.Take(5)) : ""), name);
                return;
            }
            ModelForm f9 = new ModelForm(this);
            f9.Show();
            if (gf is YdrFile ydr9) f9.LoadModel(ydr9);
            else if (gf is YddFile ydd9) f9.LoadModels(ydd9);
            int nbones = items.Max(i => i.Bones);
            string animNote = "";
            if (nbones > 0)
            {
                var clipDicts = Gta6ClipDicts;
                if (clipDicts.Count > 0) f9.SetGta6Clips(clipDicts.Keys, LoadGta6ClipDict);
                animNote = ", " + nbones + " bones" + rigNote + (clipDicts.Count > 0 ? " (type a clip dictionary name to play clips)" : " (no .zcd clip dictionaries loaded)");
            }
            else animNote = rigNote;
            var smNotes = new List<string>();
            var secMotion = new List<KeyValuePair<string, Rpf9SecMotion.Zsm>>();
            foreach (var src in secMotionSources) secMotion.AddRange(LoadGta6SecMotion(src, smNotes));
            if (secMotion.Count > 0)
            {
                int bound = f9.SetGta6SecMotion(secMotion);
                animNote += ", " + secMotion.Count + " secondary motion (" + string.Join(", ", secMotion.GroupBy(z => z.Value.Type).OrderBy(g => g.Key).Select(g => g.Count() + " " + Rpf9SecMotion.Zsm.TypeName(g.Key)))
                    + (bound < secMotion.Count ? "; " + (secMotion.Count - bound) + " with no matching bones" : "") + ")";
            }
            if (smNotes.Count > 0) notes.AddRange(smNotes);
            int ntri = items.Sum(i => i.Triangles);
            UpdateStatus(name + ": " + items.Count + " drawable(s), " + ntri.ToString("N0") + " triangles" + animNote + ", " + textured + "/" + texShaders + " shaders textured" + texNote + (notes.Count > 0 ? "; " + notes.Count + " notes: " + string.Join("; ", notes.Take(3)) : ""));
        }

        private void ViewModel(string name, string path, byte[] data, RpfFileEntry e)
        {
            if (e is Rpf9FileEntry e9)
            {
                //GTA VI drawable / drawable dictionary / fragment
                var notes = new List<string>();
                List<Rpf9Drawable.Item> items;
                string rigNote = "";
                Cursor = Cursors.WaitCursor;
                try
                {
                    var rig = LoadGta6Rig(e9, out string rigName) ?? FindGta6RigForPart(data, e9, out rigName);
                    items = Rpf9Drawable.Read(data, e9.VirtualSize, notes, rig);
                    if (items.Any(i => i.RigUse == 2)) rigNote = ", own skeleton attached to rig " + rigName;
                    else if (items.Any(i => i.RigUse == 1)) rigNote = ", skinned to rig " + rigName;
                    int miss = items.Sum(i => i.MissingBones);
                    if (miss > 0) rigNote += " (" + miss + " bones the mesh names aren't in the skeleton; open the character's .zkit for the whole outfit)";
                }
                finally { Cursor = Cursors.Default; }
                if (items.Count == 0)
                {
                    MessageBox.Show("No drawable this version understands." + (notes.Count > 0 ? "\n" + string.Join("\n", notes.Take(5)) : ""), name);
                    ViewHex(name, path, data);
                    return;
                }
                ShowGta6Model(name, e, items, notes, rigNote, new List<Rpf9FileEntry> { e9 });
                return;
            }
            var nl = e?.NameLower ?? "";
            var fe = Path.GetExtension(nl);
            ModelForm f = new ModelForm(this);
            f.Show();
            switch (fe)
            {
                case ".ydr":
                    var ydr = RpfFile.GetFile<YdrFile>(e, data);
                    f.LoadModel(ydr);
                    break;
                case ".ydd":
                    var ydd = RpfFile.GetFile<YddFile>(e, data);
                    f.LoadModels(ydd);
                    break;
                case ".yft":
                    var yft = RpfFile.GetFile<YftFile>(e, data);
                    f.LoadModel(yft);
                    break;
                case ".ybn":
                    var ybn = RpfFile.GetFile<YbnFile>(e, data);
                    f.LoadModel(ybn);
                    break;
                case ".ypt":
                    var ypt = RpfFile.GetFile<YptFile>(e, data);
                    f.LoadParticles(ypt);
                    break;
                case ".ynv":
                    var ynv = RpfFile.GetFile<YnvFile>(e, data);
                    f.LoadNavmesh(ynv);
                    break;
            }
        }
        private void ViewCut(string name, string path, byte[] data, RpfFileEntry e)
        {
            var cut = RpfFile.GetFile<CutFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(cut);
        }
        private void ViewAwc(string name, string path, byte[] data, RpfFileEntry e)
        {
            var awc = RpfFile.GetFile<AwcFile>(e, data);
            AwcForm f = new AwcForm();
            f.Show();
            f.LoadAwc(awc);
        }
        private void ViewGxt(string name, string path, byte[] data, RpfFileEntry e)
        {
            var gxt = RpfFile.GetFile<Gxt2File>(e, data);
            TextForm f = new TextForm(this);
            f.Show();
            f.LoadGxt2(name, path, gxt);
        }
        private void ViewRel(string name, string path, byte[] data, RpfFileEntry e)
        {
            var rel = RpfFile.GetFile<RelFile>(e, data);
            RelForm f = new RelForm(this);
            f.Show();
            f.LoadRel(rel);
        }
        private void ViewFxc(string name, string path, byte[] data, RpfFileEntry e)
        {
            var fxc = RpfFile.GetFile<FxcFile>(e, data);
            FxcForm f = new FxcForm();
            f.Show();
            f.LoadFxc(fxc);
        }
        private void ViewYwr(string name, string path, byte[] data, RpfFileEntry e)
        {
            var ywr = RpfFile.GetFile<YwrFile>(e, data);
            YwrForm f = new YwrForm();
            f.Show();
            f.LoadYwr(ywr);
        }
        private void ViewYvr(string name, string path, byte[] data, RpfFileEntry e)
        {
            var yvr = RpfFile.GetFile<YvrFile>(e, data);
            YvrForm f = new YvrForm();
            f.Show();
            f.LoadYvr(yvr);
        }
        private void ViewYcd(string name, string path, byte[] data, RpfFileEntry e)
        {
            if (e is Rpf9FileEntry e9)
            {
                //GTA VI clip dictionary: text summary (clips play in the model viewer, on a drawable with a skeleton)
                string txt;
                Cursor = Cursors.WaitCursor;
                try { txt = Rpf9Anim.Summary(Rpf9Anim.Read(data, e9.VirtualSize)); }
                catch (Exception ex) { txt = "Not a clip dictionary this version understands: " + ex.Message; }
                finally { Cursor = Cursors.Default; }
                TextForm tf = new TextForm(this);
                tf.Show();
                tf.LoadReadOnlyText(name + ".txt", path, txt);
                return;
            }
            var ycd = RpfFile.GetFile<YcdFile>(e, data);
            YcdForm f = new YcdForm();
            f.Show();
            f.LoadYcd(ycd);
        }
        private void ViewYnd(string name, string path, byte[] data, RpfFileEntry e)
        {
            var ynd = RpfFile.GetFile<YndFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(ynd);
        }
        private void ViewYed(string name, string path, byte[] data, RpfFileEntry e)
        {
            var yed = RpfFile.GetFile<YedFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(yed);
        }
        private void ViewYld(string name, string path, byte[] data, RpfFileEntry e)
        {
            var yld = RpfFile.GetFile<YldFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(yld);
        }
        private void ViewYfd(string name, string path, byte[] data, RpfFileEntry e)
        {
            var yfd = RpfFile.GetFile<YfdFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(yfd);
        }
        private void ViewCacheDat(string name, string path, byte[] data, RpfFileEntry e)
        {
            var cachedat = RpfFile.GetFile<CacheDatFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(cachedat);
        }
        private void ViewHeightmap(string name, string path, byte[] data, RpfFileEntry e)
        {
            var heightmap = RpfFile.GetFile<HeightmapFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(heightmap);
        }
        private void ViewMrf(string name, string path, byte[] data, RpfFileEntry e)
        {
            var mrf = RpfFile.GetFile<MrfFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(mrf);
        }
        private void ViewNametable(string name, string path, byte[] data, RpfFileEntry e)
        {
            TextForm f = new TextForm(this);
            f.Show();
            f.LoadNametable(name, path, data, e);
        }
        private void ViewDistantLights(string name, string path, byte[] data, RpfFileEntry e)
        {
            var dlf = RpfFile.GetFile<DistantLightsFile>(e, data);
            GenericForm f = new GenericForm(this);
            f.Show();
            f.LoadFile(dlf, dlf.RpfFileEntry);
        }
        private void ViewYpdb(string name, string path, byte[] data, RpfFileEntry e)
        {
            var ypdb = RpfFile.GetFile<YpdbFile>(e, data);
            MetaForm f = new MetaForm(this);
            f.Show();
            f.LoadMeta(ypdb);
        }

        private RpfFileEntry CreateFileEntry(string name, string path, ref byte[] data)
        {
            //this should only really be used when loading a file from the filesystem.
            RpfFileEntry e = null;
            uint rsc7 = (data?.Length > 4) ? BitConverter.ToUInt32(data, 0) : 0;
            if (rsc7 == 0x37435352) //RSC7 header present! create RpfResourceFileEntry and decompress data...
            {
                e = RpfFile.CreateResourceFileEntry(ref data, 0);//"version" should be loadable from the header in the data..
                data = ResourceBuilder.Decompress(data);
            }
            else
            {
                var be = new RpfBinaryFileEntry();
                be.FileSize = (uint)data?.Length;
                be.FileUncompressedSize = be.FileSize;
                e = be;
            }
            e.Name = name;
            e.NameLower = name?.ToLowerInvariant();
            e.NameHash = JenkHash.GenHash(e.NameLower);
            e.ShortNameHash = JenkHash.GenHash(Path.GetFileNameWithoutExtension(e.NameLower));
            e.Path = path;
            return e;
        }


        private Form FindExistingForm(RpfFileEntry e)
        {
            if (e == null) return null;
            var allforms = Application.OpenForms;
            var path = e.Path.ToLowerInvariant();
            foreach (var form in allforms)
            {
                var metaform = form as MetaForm;
                if (metaform?.rpfFileEntry == e) return metaform;
                if (metaform?.rpfFileEntry?.Path?.ToLowerInvariant() == path)
                    return metaform; //need to test the path as well since the file entry may have been replaced by a new version..!

            }
            return null;
        }


        private void ShowTreeContextMenu(TreeNode n, Point p)
        {
            var f = n?.Tag as MainTreeFolder;

            bool filesys = ((f.RpfFolder == null) && (f.RpfFile == null));
            bool expanded = ((n != null) && (n.IsExpanded));
            bool collapsed = ((n != null) && (!n.IsExpanded));
            bool extrafldr = ((n?.Parent == null) && (f?.IsExtraFolder ?? false));

            if ((f.RpfFile != null) && (f.RpfFile.Parent == null))
            {
                filesys = true; //allow viewing root RPF's in explorer
            }

            TreeContextWinExplorerMenu.Enabled = filesys;
            TreeContextExpandMenu.Enabled = collapsed;
            TreeContextCollapseMenu.Enabled = expanded;
            TreeContextCloseFolderSeparator.Visible = extrafldr;
            TreeContextCloseFolderMenu.Visible = extrafldr;


            TreeContextMenu.Show(MainTreeView, p);

        }

        private void ShowListContextMenu(MainListItem item)
        {

            bool isitem = false;
            bool isfile = false;
            bool isfolder = false;
            bool isarchive = false;
            bool isfilesys = false;
            bool isrpffolder = (CurrentFolder?.RpfFolder != null);
            bool issearch = CurrentFolder?.IsSearchResults ?? false;
            bool canview = false;
            bool canexportxml = false;
            bool canextract = false;
            bool canimport = EditMode && !issearch;// && isrpffolder;
            bool cancreate = EditMode && !issearch;
            bool canedit = false;
            bool candefrag = false;
            bool canpaste = EditMode && (CopiedFiles.Count > 0);

            if (item != null)
            {
                var entry = item.GetRpfEntry();
                isitem = true;
                isfilesys = (entry == null);
                isarchive = (item.Folder?.RpfFile != null);
                isfolder = (item.Folder != null);
                isfile = !isfolder;
                canview = CanViewFile(item);
                canexportxml = CanExportXml(item);
                canedit = EditMode && !issearch;
                canextract = isfile || (isarchive && !isfilesys);
                candefrag = isarchive && canedit;
            }


            ListContextViewMenu.Enabled = canview;
            ListContextViewHexMenu.Enabled = isfile;

            ListContextExportXmlMenu.Enabled = canexportxml;
            ListContextExtractRawMenu.Enabled = canextract;
            ListContextExtractUncompressedMenu.Enabled = isfile;

            ListContextNewMenu.Visible = cancreate;
            ListContextImportRawMenu.Visible = canimport;
            ListContextImportFbxMenu.Visible = canimport;
            ListContextImportXmlMenu.Visible = canimport;
            ListContextImportSeparator.Visible = cancreate;

            ListContextCopyMenu.Enabled = isfile;
            ListContextCopyPathMenu.Enabled = isitem;
            ListContextPasteMenu.Enabled = canpaste;
            ListContextPasteMenu.Visible = EditMode;

            ListContextOpenFileLocationMenu.Visible = issearch;
            ListContextOpenFileLocationSeparator.Visible = issearch;

            ListContextRenameMenu.Visible = canedit;
            ListContextDeleteMenu.Visible = canedit;
            ListContextEditSeparator.Visible = canedit;

            ListContextDefragmentMenu.Visible = candefrag;
            ListContextDefragmentSeparator.Visible = candefrag;

            ListContextMenu.Show(Cursor.Position);

        }




        private void EnableEditMode(bool enable, bool warn = true)
        {
            if (EditMode == enable)
            {
                return;
            }

            if (enable && warn)
            {
                if (MessageBox.Show(this, "While in edit mode, all changes are automatically saved.\nDo you want to continue?", "Warning - Entering edit mode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    return;
                }
            }

            EditMode = enable;
            EditModeButton.Checked = enable;
            MainListView.LabelEdit = enable;

            EnsureEditModeWarning();
            UpdateSelectionUI();
        }

        private void EnsureEditModeWarning()
        {
            var mods = CurrentFolder?.Path.StartsWith("mods", StringComparison.InvariantCultureIgnoreCase) ?? false;
            var extn = CurrentFolder?.FullPath?.StartsWith(RootFolder.FullPath, StringComparison.InvariantCultureIgnoreCase) == false;
            var srch = CurrentFolder?.IsSearchResults ?? false;
            var fsys = CurrentFolder?.RpfFolder == null;
            var game = CurrentFolder?.Path != CurrentFolder?.FullPath;
            var show = EditMode && !mods && !extn && !srch && (!fsys || game);
            var copy = show && (CurrentFolder?.RpfFolder != null);
            var gap = 3;
            var bot = MainListView.Bottom;

            EditModeBaseWarningPanel.Top = gap;
            EditModeModsWarningPanel.Top = gap;
            EditModeModsWarningPanel.Visible = false;
            CopyToModsFolderButton.Visible = copy;

            MainListView.Top = show ? EditModeBaseWarningPanel.Bottom + gap : gap;
            MainListView.Height = bot - MainListView.Top;
        }

        private void CopyToModsFolder()
        {
            if (CurrentFolder == null) return;
            var rootpath = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();
            var modspath = rootpath + "mods\\";
            var curpath = CurrentFolder.FullPath;
            var rpfpath = CurrentFolder.GetCurrentRpfFile()?.GetPhysicalFilePath();
            if (string.IsNullOrEmpty(rpfpath)) return;
            if (rpfpath.StartsWith(rootpath, StringComparison.InvariantCultureIgnoreCase) != true) return;
            var relpath = rpfpath.Substring(rootpath.Length);
            var destpath = modspath + relpath;
            var newrelpath = destpath.Substring(rootpath.Length);
            var navtarget = "mods\\" + curpath.Substring(rootpath.Length);

            if (File.Exists(destpath))
            {
                var msg = $"File {relpath} already exists in the mods folder!\nDo you want to replace it?";
                if (MessageBox.Show(msg, "File already exists", MessageBoxButtons.YesNo) != DialogResult.Yes)
                {
                    Navigate(navtarget);
                    return;
                }
            }

            Cursor = Cursors.WaitCursor;
            CopyToModsFolderButton.Enabled = false;
            Task.Run(() =>
            {
                try
                {
                    var destdir = Path.GetDirectoryName(destpath);
                    if (Directory.Exists(destdir) == false)
                    {
                        Directory.CreateDirectory(destdir);
                    }

                    if (File.Exists(destpath))
                    {
                        File.Delete(destpath);
                    }

                    File.Copy(rpfpath, destpath);

                }
                catch (Exception ex)
                {
                    Invoke(new Action(() => { MessageBox.Show("Error copying file:\n" + ex.ToString()); }));
                    CopyToModsFolderButton.Enabled = true;
                    Cursor = Cursors.Default;
                    return;
                }

                var rpf = new RpfFile(destpath, newrelpath);

                rpf.ScanStructure(UpdateStatus, UpdateErrorLog);

                if (rpf.LastException != null) //incase of corrupted rpf (or renamed NG encrypted RPF)
                {
                    Invoke(new Action(() => { MessageBox.Show("Error scanning file:\n" + rpf.LastException.ToString()); }));
                    CopyToModsFolderButton.Enabled = true;
                    Cursor = Cursors.Default;
                    return;
                }

                UpdateStatus($"Copied {newrelpath}.");

                Invoke(new Action(() =>
                {
                    Cursor = Cursors.Default;

                    var node = CreateRpfTreeFolder(rpf, newrelpath, destpath);
                    var allrpfs = new List<RpfFile>();
                    RecurseMainTreeViewRPF(node, allrpfs);
                    EnsureRpfTreeNodeHierarchy(node);

                    Navigate(navtarget);

                    CopyToModsFolderButton.Enabled = true;
                }));
            });
        }



        private bool IsFilenameOk(string name)
        {
            foreach (var ic in Path.GetInvalidFileNameChars())
            {
                if (name.Contains(ic))
                {
                    return false;
                }
            }
            return true;
        }





        public bool EnsureRpfValidEncryption(RpfFile file = null, bool recursive = false)
        {
            if ((file == null) && (CurrentFolder.RpfFolder == null)) return false;

            var rpf = file ?? CurrentFolder.RpfFolder.File;

            if (rpf == null) return false;

            if (RpfFile.IsValidEncryption(rpf, recursive)) return true;//it's already valid...

            var msgr = recursive ? "(including all its parents and children) " : "";
            var msg1 = $"Are you sure you want to change this archive {msgr}to OPEN encryption?";
            var msg2 = "Loading by the game will require a mod loader such as OpenRPF.asi or OpenIV.asi.";

            var confirm = new Func<RpfFile, bool>((f) => 
            {
                var msg0 = (f != null) ? $"Archive {f.Name} is currently set to {f.Encryption} encryption.\n" : "";
                var msg = $"{msg0}{msg1}\n{msg2}";
                return (MessageBox.Show(msg, "Change RPF encryption type", MessageBoxButtons.YesNo) == DialogResult.Yes);
            });


            if (recursive)
            {
                if (confirm(null) == false)
                {
                    return false;
                }
            }

            return RpfFile.EnsureValidEncryption(rpf, recursive ? null : confirm, recursive);
        }


        public bool EnsureCurrentFolderEditable()
        {
            if (!EditMode) return false;

            var parentrpffldr = CurrentFolder.RpfFolder;
            var parentfullpath = CurrentFolder.FullPath;
            if ((parentrpffldr == null) && (string.IsNullOrEmpty(parentfullpath)))
            {
                MessageBox.Show("No parent folder selected! This shouldn't happen. Refresh the view and try again.");
                return false;
            }

            return true;
        }


        public string SelectFolder()
        {
            var selpath = Settings.Default.RPFExplorerSelectedFolder;
            if (!string.IsNullOrEmpty(selpath))
            {
                FolderBrowserDialog.SelectedPath = selpath;
            }

            if (FolderBrowserDialog.ShowDialogNew() != DialogResult.OK) return "";
            string folderpath = FolderBrowserDialog.SelectedPath;
            if (!folderpath.EndsWith("\\")) folderpath += "\\";

            Settings.Default.RPFExplorerSelectedFolder = folderpath;

            return folderpath;
        }




        private void ViewSelected()
        {
            for (int i = 0; i < MainListView.SelectedIndices.Count; i++)
            {
                var idx = MainListView.SelectedIndices[i];
                if ((idx < 0) || (idx >= CurrentFiles.Count)) continue;
                var file = CurrentFiles[idx];
                if (file.Folder == null)
                {
                    View(file);
                }
            }
        }
        private void ViewSelectedHex()
        {
            for (int i = 0; i < MainListView.SelectedIndices.Count; i++)
            {
                var idx = MainListView.SelectedIndices[i];
                if ((idx < 0) || (idx >= CurrentFiles.Count)) continue;
                var file = CurrentFiles[idx];
                if (file.Folder == null)
                {
                    ViewHex(file);
                }
            }
        }
        private void ExportXml()
        {
            var needfolder = false;//need a folder to output ytd XML to, for the texture .dds files
            if (MainListView.SelectedIndices.Count == 1)
            {
                var idx = MainListView.SelectedIndices[0];
                if ((idx < 0) || (idx >= CurrentFiles.Count)) return;
                var file = CurrentFiles[idx];
                var nl = file?.File?.NameLower ?? file?.Name?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(nl))
                {
                    needfolder = nl.EndsWith(".ytd") || nl.EndsWith(".ydr") || nl.EndsWith(".ydd") || nl.EndsWith(".yft") || nl.EndsWith(".ypt") || nl.EndsWith(".awc") || nl.EndsWith(".fxc");
                }
            }

            if ((MainListView.SelectedIndices.Count == 1) && (!needfolder))
            {
                var errorAction = new Action<string>((msg) => MessageBox.Show(msg));

                var idx = MainListView.SelectedIndices[0];
                if ((idx < 0) || (idx >= CurrentFiles.Count)) return;
                var file = CurrentFiles[idx];
                if (file.Folder == null)
                {
                    var xml = GetFileXml(file, out var newfn, null, errorAction);
                    
                    if (string.IsNullOrEmpty(xml) == false)
                    {
                        SaveFileDialog.FileName = newfn;
                        if (SaveFileDialog.ShowDialog() == DialogResult.OK)
                        {
                            var path = SaveFileDialog.FileName;
                            try
                            {
                                File.WriteAllText(path, xml);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show("Error saving file " + path + ":\n" + ex.ToString());
                            }
                        }
                    }
                }
            }
            else
            {
                var folderpath = SelectFolder();
                if (string.IsNullOrEmpty(folderpath)) return;

                StringBuilder errors = new StringBuilder();
                var errorAction = new Action<string>((msg) => errors.AppendLine(msg));

                for (int i = 0; i < MainListView.SelectedIndices.Count; i++)
                {
                    var idx = MainListView.SelectedIndices[i];
                    if ((idx < 0) || (idx >= CurrentFiles.Count)) continue;
                    var file = CurrentFiles[idx];
                    if (file.Folder == null)
                    {
                        var xml = GetFileXml(file, out var newfn, folderpath, errorAction);
                        if (string.IsNullOrEmpty(xml)) continue;

                        var path = folderpath + newfn;
                        try
                        {
                            File.WriteAllText(path, xml);
                        }
                        catch (Exception ex)
                        {
                            errors.AppendLine("Error saving file " + path + ":\n" + ex.ToString());
                        }
                    }
                }

                string errstr = errors.ToString();
                if (!string.IsNullOrEmpty(errstr))
                {
                    MessageBox.Show("Errors were encountered:\n" + errstr);
                }
            }
        }
        private void ExtractRaw()
        {
            if (MainListView.SelectedIndices.Count == 1)
            {
                var idx = MainListView.SelectedIndices[0];
                if ((idx < 0) || (idx >= CurrentFiles.Count)) return;
                var file = CurrentFiles[idx];

                if ((file.Folder == null) || (file.Folder.RpfFile != null))
                {
                    byte[] data = GetFileDataCompressResources(file);
                    if (data == null)
                    {
                        MessageBox.Show("Unable to extract file: " + file.Path);
                        return;
                    }

                    SaveFileDialog.FileName = file.Name;
                    if (SaveFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        string path = SaveFileDialog.FileName;
                        try
                        {
                            File.WriteAllBytes(path, data);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Error saving file " + path + ":\n" + ex.ToString());
                        }
                    }
                }
            }
            else
            {
                var folderpath = SelectFolder();
                if (string.IsNullOrEmpty(folderpath)) return;

                StringBuilder errors = new StringBuilder();

                for (int i = 0; i < MainListView.SelectedIndices.Count; i++)
                {
                    var idx = MainListView.SelectedIndices[i];
                    if ((idx < 0) || (idx >= CurrentFiles.Count)) continue;
                    var file = CurrentFiles[idx];
                    if ((file.Folder == null) || (file.Folder.RpfFile != null))
                    {
                        var path = folderpath + file.Name;
                        try
                        {
                            var data = GetFileDataCompressResources(file);
                            if (data == null)
                            {
                                errors.AppendLine("Unable to extract file: " + file.Path);
                                continue;
                            }

                            File.WriteAllBytes(path, data);
                        }
                        catch (Exception ex)
                        {
                            errors.AppendLine("Error saving file " + path + ":\n" + ex.ToString());
                        }
                    }
                }

                string errstr = errors.ToString();
                if (!string.IsNullOrEmpty(errstr))
                {
                    MessageBox.Show("Errors were encountered:\n" + errstr);
                }
            }
        }
        private void ExtractUncompressed()
        {
            if (MainListView.SelectedIndices.Count == 1)
            {
                var idx = MainListView.SelectedIndices[0];
                if ((idx < 0) || (idx >= CurrentFiles.Count)) return;
                var file = CurrentFiles[idx];
                if (file.Folder == null)
                {
                    byte[] data = GetFileData(file);
                    if (data == null)
                    {
                        MessageBox.Show("Unable to extract file: " + file.Path);
                        return;
                    }

                    SaveFileDialog.FileName = file.Name;
                    if (SaveFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        string path = SaveFileDialog.FileName;
                        try
                        {
                            File.WriteAllBytes(path, data);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Error saving file " + path + ":\n" + ex.ToString());
                        }
                    }
                }
            }
            else
            {
                var folderpath = SelectFolder();
                if (string.IsNullOrEmpty(folderpath)) return;

                StringBuilder errors = new StringBuilder();

                for (int i = 0; i < MainListView.SelectedIndices.Count; i++)
                {
                    var idx = MainListView.SelectedIndices[i];
                    if ((idx < 0) || (idx >= CurrentFiles.Count)) continue;
                    var file = CurrentFiles[idx];
                    if (file.Folder == null)
                    {
                        var path = folderpath + file.Name;
                        var data = GetFileData(file);
                        if (data == null)
                        {
                            errors.AppendLine("Unable to extract file: " + file.Path);
                            continue;
                        }
                        try
                        {
                            File.WriteAllBytes(path, data);
                        }
                        catch (Exception ex)
                        {
                            errors.AppendLine("Error saving file " + path + ":\n" + ex.ToString());
                        }
                    }
                }

                string errstr = errors.ToString();
                if (!string.IsNullOrEmpty(errstr))
                {
                    MessageBox.Show("Errors were encountered:\n" + errstr);
                }
            }
        }
        private void ExtractAll()
        {
            if (CurrentFiles == null) return;
            
            var folderpath = SelectFolder();
            if (string.IsNullOrEmpty(folderpath)) return;

            StringBuilder errors = new StringBuilder();

            foreach (var file in CurrentFiles)
            {
                if ((file.Folder == null) || (file.Folder.RpfFile != null))
                {
                    var path = folderpath + file.Name;
                    try
                    {
                        var data = GetFileDataCompressResources(file);
                        if (data == null)
                        {
                            errors.AppendLine("Unable to extract file: " + file.Path);
                            continue;
                        }

                        File.WriteAllBytes(path, data);
                    }
                    catch (Exception ex)
                    {
                        errors.AppendLine("Error saving file " + path + ":\n" + ex.ToString());
                    }
                }
            }

            string errstr = errors.ToString();
            if (!string.IsNullOrEmpty(errstr))
            {
                MessageBox.Show("Errors were encountered:\n" + errstr);
            }
        }
        private void NewFolder()
        {
            if (CurrentFolder == null) return;//shouldn't happen
            if (CurrentFolder?.IsSearchResults ?? false) return;

            string fname = Prompt.ShowDialog(this, "Enter a name for the new folder:", "Create folder", "folder");
            if (string.IsNullOrEmpty(fname))
            {
                return;//no name was provided.
            }

            var fnames = fname.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            RpfDirectoryEntry newdir = null;
            MainTreeFolder node = null;
            MainTreeFolder cnode = null;
            string cpath = (string.IsNullOrEmpty(CurrentFolder.Path) ? "" : CurrentFolder.Path + "\\");
            var rootpath = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();
            var csubpath = "";

            foreach (var name in fnames)
            {
                if (!IsFilenameOk(name)) break; //new name contains invalid char(s). don't continue

                csubpath += name;

                string relpath = cpath + csubpath.ToLowerInvariant();
                string fullpath = rootpath + relpath;

                try
                {
                    if (CurrentFolder.RpfFolder != null)
                    {
                        if (!EnsureRpfValidEncryption()) return;

                        //create new directory entry in the RPF.

                        newdir = RpfFile.CreateDirectory(newdir ?? CurrentFolder.RpfFolder, name);

                        var newnode = CreateRpfDirTreeFolder(newdir, relpath, fullpath);

                        if (node == null)
                        {
                            node = newnode;
                            cnode = newnode;
                        }
                        else
                        {
                            cnode.AddChild(newnode);
                            cnode = newnode;
                        }
                    }
                    else
                    {
                        //create a folder in the filesystem.
                        if (Directory.Exists(fullpath))
                        {
                            throw new Exception("Folder " + fullpath + " already exists!");
                        }
                        Directory.CreateDirectory(fullpath);

                        var newnode = CreateRootDirTreeFolder(name, relpath, fullpath);
                        if (node == null)
                        {
                            node = newnode;
                            cnode = newnode;
                        }
                        else
                        {
                            cnode.AddChild(newnode);
                            cnode = newnode;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error creating new folder: " + ex.Message, "Unable to create new folder");
                    return;
                }

                csubpath += "\\";
            }


            if (node != null)
            {
                AddNewFolderTreeNode(node);
            }

        }
        private void NewRpfArchive()
        {
            if (CurrentFolder == null) return;//shouldn't happen
            if (CurrentFolder?.IsSearchResults ?? false) return;

            string fname = Prompt.ShowDialog(this, "Enter a name for the new archive:", "Create RPF7 archive", "new");
            if (string.IsNullOrEmpty(fname))
            {
                return;//no name was provided.
            }
            if (!IsFilenameOk(fname)) return; //new name contains invalid char(s). don't do anything

            if (!fname.ToLowerInvariant().EndsWith(".rpf"))
            {
                fname = fname + ".rpf";//make sure it ends with .rpf
            }
            string cpath = (string.IsNullOrEmpty(CurrentFolder.Path) ? "" : CurrentFolder.Path + "\\");
            string relpath = cpath + fname.ToLowerInvariant();


            RpfEncryption encryption = RpfEncryption.OPEN;//TODO: select encryption mode

            RpfFile newrpf = null;

            try
            {
                if (CurrentFolder.RpfFolder != null)
                {
                    if (!EnsureRpfValidEncryption()) return;

                    //adding a new RPF as a child of another
                    newrpf = RpfFile.CreateNew(CurrentFolder.RpfFolder, fname, encryption);
                }
                else
                {
                    //adding a new RPF in the filesystem
                    newrpf = RpfFile.CreateNew(GTAFolder.CurrentGTAFolder, relpath, encryption);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error creating archive: " + ex.Message, "Unable to create new archive");
                return;
            }


            if (newrpf != null)
            {
                var node = CreateRpfTreeFolder(newrpf, newrpf.Path, GTAFolder.GetCurrentGTAFolderWithTrailingSlash() + newrpf.Path);
                RecurseMainTreeViewRPF(node, AllRpfs);
                AddNewFolderTreeNode(node);
            }

        }
        private void NewYtdFile()
        {
            if (CurrentFolder == null) return;//shouldn't happen
            if (CurrentFolder?.IsSearchResults ?? false) return;

            string fname = Prompt.ShowDialog(this, "Enter a name for the new YTD file:", "Create YTD (Texture Dictionary)", "new");
            if (string.IsNullOrEmpty(fname))
            {
                return;//no name was provided.
            }
            if (!IsFilenameOk(fname)) return; //new name contains invalid char(s). don't do anything

            if (!fname.ToLowerInvariant().EndsWith(".ytd"))
            {
                fname = fname + ".ytd";//make sure it ends with .ytd
            }

            var ytd = new YtdFile();
            ytd.TextureDict = new TextureDictionary();
            ytd.TextureDict.Textures = new ResourcePointerList64<Texture>();
            ytd.TextureDict.TextureNameHashes = new ResourceSimpleList64_uint();
            var data = ytd.Save();

            if (CurrentFolder.RpfFolder != null) //create in RPF archive
            {
                RpfFile.CreateFile(CurrentFolder.RpfFolder, fname, data);
            }
            else //create in filesystem
            {
                var outfpath = Path.Combine(CurrentFolder.FullPath, fname);
                File.WriteAllBytes(outfpath, data);
                CurrentFolder.EnsureFile(outfpath);
            }

            RefreshMainListView();
        }
        private void ImportFbx()
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;

            if (!EnsureCurrentFolderEditable()) return;

            if (!EnsureRpfValidEncryption() && (CurrentFolder.RpfFolder != null)) return;


            OpenFileDialog.Filter = "FBX Files|*.fbx";
            if (OpenFileDialog.ShowDialog(this) != DialogResult.OK)
            {
                return;//canceled
            }

            var fpaths = OpenFileDialog.FileNames;
            var fdict = new Dictionary<string, byte[]>();

            foreach (var fpath in fpaths)
            {
#if !DEBUG
                try
#endif
                {
                    if (!File.Exists(fpath))
                    {
                        continue;//this shouldn't happen...
                    }

                    var fi = new FileInfo(fpath);
                    var fname = fi.Name;
                    var fnamel = fname.ToLowerInvariant();
                    var trimlength = 4;

                    if (!fnamel.EndsWith(".fbx"))
                    {
                        MessageBox.Show(fname + ": Not an FBX file!", "Cannot import FBX");
                        continue;
                    }

                    fname = fname.Substring(0, fname.Length - trimlength);

                    var data = File.ReadAllBytes(fpath);
                    fdict[fname] = data;

                }
#if !DEBUG
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Unable to read file " + fpath);
                }
#endif

            }


            var fbxForm = new ImportFbxForm();
            fbxForm.SetInputFiles(fdict);
            fbxForm.ShowDialog();

            if (fbxForm.DialogResult != DialogResult.OK)
            {
                return; //fbx import canceled
            }

            var converted = fbxForm.GetOutputFiles();
            if (converted == null)
            {
                return;
            }

            foreach (var kvp in converted)
            {
                var fname = kvp.Key;
                var data = kvp.Value;
                if (data != null)
                {
                    if (CurrentFolder.RpfFolder != null)
                    {
                        RpfFile.CreateFile(CurrentFolder.RpfFolder, fname, data);
                    }
                    else if (!string.IsNullOrEmpty(CurrentFolder.FullPath))
                    {
                        var outfpath = Path.Combine(CurrentFolder.FullPath, fname);
                        File.WriteAllBytes(outfpath, data);
                        CurrentFolder.EnsureFile(outfpath);
                    }

                }
            }


            RefreshMainListView();

        }
        private void ImportXmlDialog()
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;

            if (!EnsureCurrentFolderEditable()) return;

            if (!EnsureRpfValidEncryption() && (CurrentFolder.RpfFolder != null)) return;

            OpenFileDialog.Filter = "XML Files|*.xml";
            if (OpenFileDialog.ShowDialog(this) != DialogResult.OK) return;
            ImportXml(OpenFileDialog.FileNames, false, null);//already checked encryption before the file dialog...
        }
        private void ImportXml(string[] fpaths, bool checkEncryption = true, Dictionary<string, RpfDirectoryEntry> dirdict = null)
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;

            if (!EnsureCurrentFolderEditable()) return;

            if (checkEncryption)
            {
                if (!EnsureRpfValidEncryption() && (CurrentFolder.RpfFolder != null)) return;
            }

            foreach (var fpath in fpaths)
            {
#if !DEBUG
                try
#endif
                {
                    if (!File.Exists(fpath))
                    {
                        continue;//this shouldn't happen...
                    }

                    var fi = new FileInfo(fpath);
                    var fname = fi.Name;
                    var fnamel = fname.ToLowerInvariant();
                    var fpathin = fpath;

                    if (!fnamel.EndsWith(".xml"))
                    {
                        MessageBox.Show(fname + ": Not an XML file!", "Cannot import XML");
                        continue;
                    }

                    byte[] data = null;
                    var mformat = MetaFormat.XML;
                    if (fnamel.IndexOf('.') == fnamel.LastIndexOf('.'))
                    {
                        //the user has selected import XML option, but this file is just an ordinary XML file.
                        //import this file directly instead of attempting XML conversion.

                        data = File.ReadAllBytes(fpath);

                    }
                    else
                    {
                        var trimlength = 4;
                        mformat = XmlMeta.GetXMLFormat(fnamel, out trimlength);

                        fname = fname.Substring(0, fname.Length - trimlength);
                        fnamel = fnamel.Substring(0, fnamel.Length - trimlength);
                        fpathin = fpathin.Substring(0, fpathin.Length - trimlength);
                        fpathin = Path.Combine(Path.GetDirectoryName(fpathin), Path.GetFileNameWithoutExtension(fpathin));

                        var doc = new XmlDocument();
                        string text = File.ReadAllText(fpath);
                        if (!string.IsNullOrEmpty(text))
                        {
                            doc.LoadXml(text);
                        }

                        data = XmlMeta.GetData(doc, mformat, fpathin);

                    }


                    if (data != null)
                    {
                        if (CurrentFolder.RpfFolder != null)
                        {
                            var rpffldr = CurrentFolder.RpfFolder;
                            if ((dirdict != null) && dirdict.ContainsKey(fpath))
                            {
                                rpffldr = dirdict[fpath];
                            }

                            RpfFile.CreateFile(rpffldr, fname, data);
                        }
                        else if (!string.IsNullOrEmpty(CurrentFolder.FullPath))
                        {
                            var outfpath = Path.Combine(CurrentFolder.FullPath, fname);
                            File.WriteAllBytes(outfpath, data);
                            CurrentFolder.EnsureFile(outfpath);

                            //TODO: handle folders...

                        }
                    }
                    else
                    {
                        MessageBox.Show(fname + ": Schema not supported.", "Cannot import " + XmlMeta.GetXMLFormatName(mformat));
                    }

                }
#if !DEBUG
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Unable to import file");
                }
#endif

            }

            RefreshMainListView();

        }
        private void ImportRaw()
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;

            if (!EnsureRpfValidEncryption() && (CurrentFolder.RpfFolder != null)) return;

            OpenFileDialog.Filter = string.Empty;
            if (OpenFileDialog.ShowDialog(this) != DialogResult.OK)
            {
                return;//canceled
            }

            var fpaths = OpenFileDialog.FileNames;

            ImportRaw(fpaths, false, null);//already checked encryption before the file dialog...
        }
        private void ImportRaw(string[] fpaths, bool checkEncryption = true, Dictionary<string, RpfDirectoryEntry> dirdict = null)
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;

            if (!EnsureCurrentFolderEditable()) return;

            if (checkEncryption)
            {
                if (!EnsureRpfValidEncryption() && (CurrentFolder.RpfFolder != null)) return;
            }

            foreach (var fpath in fpaths)
            {
                try
                {
                    if (!File.Exists(fpath))
                    {
                        continue;//this shouldn't happen...
                    }

                    var fi = new FileInfo(fpath);
                    var fname = fi.Name;

                    if (fi.Length > 0x3FFFFFFF)
                    {
                        MessageBox.Show("File " + fname + " is too big! Max 1GB supported.", "Unable to import file");
                        continue;
                    }

                    Cursor = Cursors.WaitCursor;

                    byte[] data = File.ReadAllBytes(fpath);

                    if (CurrentFolder.RpfFolder != null)
                    {
                        var rpffldr = CurrentFolder.RpfFolder;
                        if ((dirdict != null) && dirdict.ContainsKey(fpath))
                        {
                            rpffldr = dirdict[fpath];
                        }

                        var entry = RpfFile.CreateFile(rpffldr, fname, data);

                        EnsureImportedRpf(entry, rpffldr); //make sure structure is created if an RPF was imported
                    }
                    else
                    {

                        var outfpath = Path.Combine(CurrentFolder.FullPath, fname);
                        File.WriteAllBytes(outfpath, data);
                        CurrentFolder.EnsureFile(outfpath);

                        //TODO: handle folders...
                    }

                    Cursor = Cursors.Default;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Unable to import file");
                }
            }

            RefreshMainListView();
        }
        private void ImportFolderStructure(string[] fpaths, List<string> filelist, Dictionary<string, RpfDirectoryEntry> dirdict)
        {
            //ensure folder structure is created for given directories in fpaths.
            //paths that are files and not directories will be ignored.
            //all will be imported to the CurrentFolder.
            //filelist is an output list of all files to import.
            //dirdict is an output dictionary of any directories that were created.

            if (filelist == null) filelist = new List<string>();
            if (dirdict == null) dirdict = new Dictionary<string, RpfDirectoryEntry>();

            foreach (var fpath in fpaths)
            {
                try
                {
                    if (File.Exists(fpath))
                    {
                        filelist.Add(fpath);
                    }
                    else if (Directory.Exists(fpath))
                    {
                        if (CurrentFolder.RpfFolder != null)
                        {
                            //create imported directory structure in the RPF.
                            var fdi = new DirectoryInfo(fpath);
                            var direntry = RpfFile.CreateDirectory(CurrentFolder.RpfFolder, fdi.Name);//create the first directory entry.
                            dirdict[fpath] = direntry;
                            var dirpaths = Directory.GetFileSystemEntries(fpath, "*", SearchOption.AllDirectories);
                            var newfiles = new List<string>();
                            foreach (var dirpath in dirpaths)
                            {
                                if (File.Exists(dirpath))
                                {
                                    newfiles.Add(dirpath);
                                }
                                else if (Directory.Exists(dirpath))
                                {
                                    var cdi = new DirectoryInfo(dirpath);
                                    RpfDirectoryEntry pdirentry;
                                    if (!dirdict.TryGetValue(cdi.Parent.FullName, out pdirentry))
                                    {
                                        pdirentry = direntry;//fallback, shouldn't get here
                                    }
                                    if (pdirentry != null)
                                    {
                                        var cdirentry = RpfFile.CreateDirectory(pdirentry, cdi.Name);
                                        dirdict[dirpath] = cdirentry;
                                    }
                                }
                            }
                            foreach (var newfile in newfiles)
                            {
                                var nfi = new FileInfo(newfile);
                                RpfDirectoryEntry ndirentry;
                                if (!dirdict.TryGetValue(nfi.DirectoryName, out ndirentry))
                                {
                                    ndirentry = direntry;//fallback, shouldn't get here
                                }
                                filelist.Add(newfile);
                                dirdict[newfile] = ndirentry;
                            }

                            EnsureImportedFolder(direntry, CurrentFolder.RpfFolder);
                        }
                        else
                        {
                            //TODO: create imported directory structure in file system...
                        }
                    }
                    else
                    { } //nothing to see here!
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"{fpath}\n{ex.Message}", "Unable to import folder");
                }
            }

        }
        private void CopySelected()
        {
            CopiedFiles.Clear();
            if (MainListView.SelectedIndices.Count <= 0) return;
            var fnames = new StringBuilder();
            foreach (int idx in MainListView.SelectedIndices)
            {
                if ((idx < 0) || (idx >= CurrentFiles.Count)) continue;
                var f = CurrentFiles[idx];
                CopiedFiles.Add(f);
                if (fnames.Length > 0) fnames.AppendLine();
                fnames.Append(f.Name);
            }
            if (fnames.Length > 0)
            {
                Clipboard.SetText(fnames.ToString());
            }
            UpdateStatus(CopiedFiles.Count.ToString() + " item" + ((CopiedFiles.Count != 1) ? "s" : "") + " copied");
        }
        private void CopyPath()
        {
            if (MainListView.SelectedIndices.Count == 0)
            {
                Clipboard.SetText(CurrentFolder?.FullPath ?? GTAFolder.GetCurrentGTAFolderWithTrailingSlash());
            }
            else if (MainListView.SelectedIndices.Count == 1)
            {
                var idx = MainListView.SelectedIndices[0];
                if ((idx < 0) || (idx >= CurrentFiles.Count)) return;
                var f = CurrentFiles[idx];
                if (f.FullPath != null)
                {
                    Clipboard.SetText(f.FullPath);
                }
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                foreach (int idx in MainListView.SelectedIndices)
                {
                    if ((idx < 0) || (idx >= CurrentFiles.Count)) return;
                    var f = CurrentFiles[idx];
                    if (f.FullPath != null)
                    {
                        sb.AppendLine(f.FullPath);
                    }
                }
                Clipboard.SetText(sb.ToString());
            }
        }
        private void CopyPath(string path)
        {
            Clipboard.SetText(path);
        }
        private void CopyFileList()
        {
            StringBuilder sb = new StringBuilder();
            if (CurrentFiles != null)
            {
                foreach (var file in CurrentFiles)
                {
                    sb.AppendLine(file.Name);
                }
            }
            Clipboard.SetText(sb.ToString());
        }
        private void RenameSelected()
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;
            if (MainListView.SelectedIndices.Count != 1) return;
            var idx = MainListView.SelectedIndices[0];
            if ((CurrentFiles != null) && (CurrentFiles.Count > idx))
            {
                var item = CurrentFiles[idx];
                string newname = Prompt.ShowDialog(this, "Enter the new name for this item:", "Rename item", item.Name);
                if (!string.IsNullOrEmpty(newname))
                {
                    RenameItem(item, newname);
                }
            }
        }
        private void RenameItem(MainListItem item, string newname)
        {
            if (!EditMode) return;
            if (item.Name == newname) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;
            if (!IsFilenameOk(newname)) return; //new name contains invalid char(s). don't do anything


            RpfFile file = item.Folder?.RpfFile;
            RpfEntry entry = item.GetRpfEntry();

            try
            {
                if (file != null)
                {
                    //updates all items in the RPF with the new path - no actual file changes made here
                    RpfFile.RenameArchive(file, newname);
                }
                if (entry != null)
                {
                    if (!EnsureRpfValidEncryption()) return;

                    //renaming an entry in an RPF
                    RpfFile.RenameEntry(entry, newname);
                }
                else
                {
                    //renaming a filesystem item...
                    var dirinfo = new DirectoryInfo(item.FullPath);
                    var newpath = Path.Combine(dirinfo.Parent.FullName, newname);
                    if (item.FullPath.ToLowerInvariant() == newpath.ToLowerInvariant())
                    {
                        return;//filesystem tends to be case-insensitive... paths are the same
                    }
                    if ((item.Folder != null) && (item.Folder.RpfFile == null))
                    {
                        //renaming a filesystem folder...
                        Directory.Move(item.FullPath, newpath);
                    }
                    else
                    {
                        //renaming a filesystem file...
                        File.Move(item.FullPath, newpath);
                    }
                }

                if (item.Folder != null)
                {
                    RenameTreeFolder(item.Folder, newname);
                }

                RenameListItem(item, newname);

            }
            catch (Exception ex)
            {
                MessageBox.Show("Error renaming " + item.Path + ": " + ex.Message, "Unable to rename item");
                return;
            }

        }
        private void DeleteSelected()
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;
            if (MainListView.SelectedIndices.Count <= 0) return;
            //if (MainListView.SelectedIndices.Count == 1) //is confirmation always really necessary?
            //{
            //    var item = CurrentFiles[MainListView.SelectedIndices[0]];
            //    if (MessageBox.Show("Are you sure you want to permantly delete " + item.Name + "?\nThis cannot be undone.", "Confirm delete", MessageBoxButtons.YesNo) != DialogResult.Yes)
            //    {
            //        return;
            //    }
            //}
            //else
            //{
            //    if (MessageBox.Show("Are you sure you want to permantly delete " + MainListView.SelectedIndices.Count.ToString() + " items?\nThis cannot be undone.", "Confirm delete", MessageBoxButtons.YesNo) != DialogResult.Yes)
            //    {
            //        return;
            //    }
            //}
            var delitems = new List<MainListItem>();
            foreach (int idx in MainListView.SelectedIndices)
            {
                if ((idx < 0) || (idx >= CurrentFiles.Count)) return;
                var f = CurrentFiles[idx];//this could change when deleting.. so need to use the temp list
                delitems.Add(f);
            }
            foreach (var f in delitems)
            {
                DeleteItem(f);
            }
        }
        private void DeleteItem(MainListItem item)
        {
            try
            {
                if (item.Folder?.RpfFile != null)
                {
                    //confirm deletion of RPF archives, just to be friendly.
                    if (MessageBox.Show("Are you sure you want to delete this archive?\n" + item.Path, "Confirm delete archive", MessageBoxButtons.YesNo) != DialogResult.Yes)
                    {
                        return;
                    }
                }
                else if ((item.Folder?.GetItemCount() ?? 0) > 0)
                {
                    //confirm deletion of non-empty folders, just to be friendly.
                    if (MessageBox.Show("Are you sure you want to delete this folder and all its contents?\n" + item.Path, "Confirm delete folder", MessageBoxButtons.YesNo) != DialogResult.Yes)
                    {
                        return;
                    }
                }

                var parent = item.Parent;
                if (parent.RpfFolder != null)
                {
                    //delete an item in an RPF.
                    if (!EnsureRpfValidEncryption()) return;

                    RpfEntry entry = item.GetRpfEntry();

                    RpfFile.DeleteEntry(entry);
                }
                else
                {
                    //delete an item in the filesystem.
                    if ((item.Folder != null) && (item.Folder.RpfFile == null))
                    {
                        Directory.Delete(item.FullPath);
                    }
                    else
                    {
                        File.Delete(item.FullPath);

                        item.Parent?.RemoveFile(item.FullPath);
                    }
                }


                if (item.Folder != null)
                {
                    RemoveTreeFolder(item.Folder);
                }

                RemoveListItem(item);

            }
            catch (Exception ex)
            {
                MessageBox.Show("Error deleting " + item.Path + ": " + ex.Message, "Unable to delete " + item.Name);
                return;
            }
        }
        private void DefragmentSelected()
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;
            if (MainListView.SelectedIndices.Count != 1)
            {
                MessageBox.Show("Can only defragment one item at a time. Please have only one item selected.");
                return;
            }
            var idx = MainListView.SelectedIndices[0];
            if ((CurrentFiles == null) || (CurrentFiles.Count <= idx))
            {
                return;
            }

            var item = CurrentFiles[idx];
            var rpf = item.Folder?.RpfFile;
            if (rpf == null)
            {
                MessageBox.Show("Can only defragment RPF archives!");
                return;
            }

            Form form = new Form() {
                Width = 450,
                Height = 290,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = "Defragment RPF Archive - CodeWalker by dexyfex",
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };
            Control addCtrl(Control c) { form.Controls.Add(c); return c; }
            Control addLabel(int y, string t)
            {
                return addCtrl(new Label() { Left = 30, Top = y, Width = 370, Height = 20, Text = t });
            }
            Control addCheckbox(int y, string t, bool defaultVal)
            {
                return addCtrl(new CheckBox() { Left = 33, Top = y, Width = 370, Height = 20, Text = t, Checked = defaultVal });
            }
            var rpfNameLabel = addLabel(20, "Archive: " + rpf.Path);
            var curSizeLabel = addLabel(40, string.Empty);
            var newSizeLabel = addLabel(60, string.Empty);
            var redSizeLabel = addLabel(80, string.Empty);
            var statusLabel = addLabel(110, string.Empty);
            var recursiveCheckbox = addCheckbox(130, "Recursive", true) as CheckBox;
            var progressBar = addCtrl(new ProgressBar() { Left = 30, Top = 180, Width = 370, Height = 20, Minimum = 0, Maximum = 1000, MarqueeAnimationSpeed = 50 }) as ProgressBar;
            var beginButton = addCtrl(new Button() { Text = "Begin Defragment", Left = 30, Top = 210, Width = 120 }) as Button;
            var closeButton = addCtrl(new Button() { Text = "Close", Left = 320, Top = 210, Width = 80 }) as Button;
            var inProgress = false;
            void updateProgress(string s, float p)
            {
                form.Invoke(new Action(() => {
                    statusLabel.Text = s;
                    progressBar.Value = Math.Max(0, Math.Min((int)(p * 1000), 1000));//p in range 0..1
                }));
            }
            void updateSizeLabels(bool init)
            {
                var curSize = rpf.FileSize;
                var newSize = rpf.GetDefragmentedFileSize(recursiveCheckbox.Checked);
                var redSize = curSize - newSize;
                curSizeLabel.Text = "Archive current size: " + TextUtil.GetBytesReadable(curSize);
                newSizeLabel.Text = "Defragmented size: " + TextUtil.GetBytesReadable(newSize);
                redSizeLabel.Text = "Size reduction: " + TextUtil.GetBytesReadable(redSize);
                if (init) statusLabel.Text = (redSize <= 0) ? "Defragmentation not required." : "Ready to defragment.";
            }
            void enableUi(bool enable)
            {
                form.Invoke(new Action(() => {
                    beginButton.Enabled = enable;
                    closeButton.Enabled = enable;
                }));
            }
            void defragment(bool recursive)
            {
                Task.Run(() => {
                    if (inProgress) return;
                    if (!EnsureRpfValidEncryption(rpf, recursive)) return;
                    inProgress = true;
                    enableUi(false);
                    RpfFile.Defragment(rpf, updateProgress, recursive);
                    updateProgress("Defragment complete.", 1.0f);
                    enableUi(true);
                    form.Invoke(new Action(() => { updateSizeLabels(false); }));
                    inProgress = false;
                });
            }
            updateSizeLabels(true);
            beginButton.Click += (sender, e) => { defragment(recursiveCheckbox.Checked); };
            closeButton.Click += (sender, e) => { form.Close(); };
            recursiveCheckbox.Click += (sender, e) => { updateSizeLabels(false); };
            form.FormClosing += (s, e) => { e.Cancel = inProgress; };
            form.ShowDialog(this);
            RefreshMainListView();
        }
        private void SelectAll()
        {
            MainListView.SelectAllItems();
            UpdateSelectionUI();
        }
        private void ShowInExplorer(string path)
        {
            try
            {
                Process.Start("explorer", "/select, \"" + path + "\"");
            }
            catch (Exception ex)
            {
                UpdateErrorLog(ex.ToString());
            }
        }
        private void OpenFileLocation()
        {
            var ind = -1;
            if (MainListView.SelectedIndices.Count == 1)
            {
                ind = MainListView.SelectedIndices[0];
            }
            if (ind < 0) return;
            if ((CurrentFiles != null) && (CurrentFiles.Count > ind))
            {
                var file = CurrentFiles[ind];
                var path = file.Path.Replace('/', '\\');
                var bsind = path.LastIndexOf('\\');
                if (bsind > 0)
                {
                    path = path.Substring(0, bsind);
                }
                Navigate(path);
                for (int i = 0; i < CurrentFiles?.Count; i++)
                {
                    if (CurrentFiles[i].Name == file.Name)
                    {
                        MainListView.BeginUpdate();
                        MainListView.SelectedIndices.Clear();
                        MainListView.SelectedIndices.Add(i);
                        MainListView.EnsureVisible(i);
                        MainListView.EndUpdate();
                        break;
                    }
                }
            }
        }
        private void OpenFolder()
        {
            var folderPath = SelectFolder();
            if (string.IsNullOrEmpty(folderPath)) return;
            if (!Directory.Exists(folderPath)) return;

            foreach (var folder in ExtraRootFolders)
            {
                if (folder.FullPath == folderPath) return;
            }

            var root = new MainTreeFolder();
            root.FullPath = folderPath;
            root.Path = folderPath;
            root.Name = Path.GetFileName(Path.GetDirectoryName(folderPath));
            root.IsExtraFolder = true;
            ExtraRootFolders.Add(root);

            Task.Run(() =>
            {
                RefreshMainTreeViewRoot(root);

                Invoke(new Action(() => 
                {
                    MainTreeView.SelectedNode = root.TreeNode;
                }));
            });
        }
        private void CloseFolder(MainTreeFolder folder)
        {
            if (folder == null) return;
            if (folder.IsExtraFolder == false) return;

            folder.TreeNode.Remove();
            ExtraRootFolders.Remove(folder);
        }
        private void Paste()
        {
            if (!EditMode) return;
            if (CopiedFiles.Count == 0) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;

            if (!EnsureCurrentFolderEditable()) return;

            if (!EnsureRpfValidEncryption() && (CurrentFolder.RpfFolder != null)) return;

            foreach (var file in CopiedFiles)
            {
                if (file.Parent?.FullPath == CurrentFolder?.FullPath) continue; //don't try to paste into the same folder
                if ((file.Folder == null) || (file.Folder.RpfFile != null)) //it's a file (not a folder) [todo: copy/paste folders..]
                {
                    var data = GetFileDataCompressResources(file);
                    if (data != null)
                    {
                        var fname = file.Name;
                        if (CurrentFolder.RpfFolder != null)
                        {
                            var rpffldr = CurrentFolder.RpfFolder;
                            var entry = RpfFile.CreateFile(rpffldr, fname, data);

                            EnsureImportedRpf(entry, rpffldr); //make sure structure is created if an RPF was imported
                        }
                        else
                        {
                            var outfpath = Path.Combine(CurrentFolder.FullPath, fname);
                            File.WriteAllBytes(outfpath, data);
                            CurrentFolder.EnsureFile(outfpath);

                            //TODO: folders...
                        }

                    }
                }
            }


            RefreshMainListView();


        }


        private void SetView(View v)
        {

            MainListView.View = v;

            foreach (var item in ViewMenu.DropDownItems)
            {
                var menu = item as ToolStripMenuItem;
                if (menu != null)
                {
                    menu.Checked = false;
                }
            }

            switch (v)
            {
                case System.Windows.Forms.View.LargeIcon:
                    ViewLargeIconsMenu.Checked = true;
                    break;
                case System.Windows.Forms.View.SmallIcon:
                    ViewSmallIconsMenu.Checked = true;
                    break;
                case System.Windows.Forms.View.List:
                    ViewListMenu.Checked = true;
                    break;
                case System.Windows.Forms.View.Details:
                    ViewDetailsMenu.Checked = true;
                    break;
            }

        }





        private string GetDropFolder()
        {
            return Path.Combine(Path.GetTempPath(), "CodeWalkerDrop");
        }
        private string CreateDropFolder()
        {
            string drop = GetDropFolder();
            if (!Directory.Exists(drop))
            {
                Directory.CreateDirectory(drop);
            }
            string dir = Path.Combine(drop, Guid.NewGuid().ToString());
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            DropFolderCreated = true;
            return dir;
        }
        private void CleanupDropFolder()
        {
            if (DropFolderCreated == false) return;
            string drop = GetDropFolder();
            if (Directory.Exists(drop))
            {
                try
                {
                    Directory.Delete(drop, true); //so broken :[
                }
                catch { } //not much we can do here...
            }
        }



        protected override void WndProc(ref Message m)
        {
            //handle back/forward buttons globally for all the form
            if (m.Msg == 0x319) //WM_APPCOMMAND
            {
                var cmd = (m.LParam.ToInt64() >> 16) & 0xFFF;
                if (cmd == 1) GoBack(); //APPCOMMAND_BROWSER_BACKWARD
                if (cmd == 2) GoForward(); //APPCOMMAND_BROWSER_FORWARD
            }
            base.WndProc(ref m);
        }


        private void ExploreForm_Load(object sender, EventArgs e)
        {
            Init();
        }

        private void ExploreForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            CleanupDropFolder();
            SaveSettings();
        }

        private void MainTreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            var prev = CurrentFolder;
            CurrentFolder = e.Node?.Tag as MainTreeFolder;
            LocationTextBox.Text = CurrentFolder?.Path ?? "";
            if (!string.IsNullOrEmpty(CurrentFolder?.SearchTerm))
            {
                SearchTextBox.Text = CurrentFolder.SearchTerm;
            }
            RefreshMainListView();
            NavigateComplete(prev);
        }

        private void MainTreeView_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {

                // Point where the mouse is clicked.
                Point p = new Point(e.X, e.Y);

                // Get the node that the user has clicked.
                TreeNode node = MainTreeView.GetNodeAt(p);
                if (node != null)
                {

                    // Select the node the user has clicked.
                    // The node appears selected until the menu is displayed on the screen.
                    //var oldNode = MainTreeView.SelectedNode;
                    MainTreeView.SelectedNode = node;

                    ShowTreeContextMenu(node, p);

                    // Highlight the selected node.
                    //MainTreeView.SelectedNode = oldNode;
                }
            }
        }

        private void MainListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var lvi = new ListViewItem();
            if ((CurrentFiles != null) && (e.ItemIndex < CurrentFiles.Count))
            {
                var file = CurrentFiles[e.ItemIndex];
                var fld = file.Folder;
                lvi.Tag = file;
                lvi.Text = file.Name;
                lvi.ToolTipText = file.Path;
                lvi.ImageIndex = file.ImageIndex;
                lvi.SubItems.Add(file.FileTypeText); //type column
                lvi.SubItems.Add(file.FileSizeText); //size column
                lvi.SubItems.Add(file.Attributes); //attributes column
                lvi.SubItems.Add(file.Path); //path column
            }
            e.Item = lvi;
        }

        private void MainListView_SearchForVirtualItem(object sender, SearchForVirtualItemEventArgs e)
        {
            if (CurrentFiles == null) return;
            var term = e.Text;
            if (string.IsNullOrEmpty(term)) return;
            var terml = term.ToLowerInvariant();
            var start = e.StartIndex;
            var isprefix = true;// e.IsPrefixSearch;
            var direction = e.Direction;
            var up = (direction == SearchDirectionHint.Up) || (direction == SearchDirectionHint.Left);
            var icnt = CurrentFiles.Count;
            var incr = up ? -1 : 1;
            var i = start;
            while ((i >= 0) && (i < icnt))
            {
                var name = CurrentFiles[i]?.Name;
                if (string.IsNullOrEmpty(name) == false)
                {
                    if (isprefix)
                    {
                        if (name.StartsWith(terml, StringComparison.InvariantCultureIgnoreCase))
                        {
                            e.Index = i;
                            return;
                        }
                    }
                    else
                    {
                        if (name.ToLowerInvariant().Contains(terml))//they fixed this in .net8
                        {
                            e.Index = i;
                            return;
                        }
                    }
                }
                i += incr;
            }
        }

        private void MainListView_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            var newdir = SortDirection;
            var idx = e.Column;
            if (idx == SortColumnIndex)
            {
                switch (SortDirection)
                {
                    case SortOrder.None: newdir = SortOrder.Ascending; break;
                    case SortOrder.Ascending: newdir = SortOrder.Descending; break;
                    case SortOrder.Descending: newdir = SortOrder.Ascending; break;
                }
            }

            SortMainListView(idx, newdir);
        }

        private void MainListView_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                MainListItem item = null;
                if ((MainListView.FocusedItem != null) && (MainListView.FocusedItem.Bounds.Contains(e.Location)))
                {
                    item = MainListView.FocusedItem.Tag as MainListItem;
                }
                ShowListContextMenu(item);
            }

            UpdateSelectionUI(); //need to use this instead of SelectedIndexChanged because of shift-click bug :/
        }

        private void MainListView_KeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (e.Control && !e.Shift);
            var ctrlshft = (e.Control && e.Shift);
            var shft = (e.Shift && !e.Control);

            switch (e.KeyCode)
            {
                case Keys.P:
                    if (ctrl) ViewSelected();
                    break;
                case Keys.H:
                    if (ctrl) ViewSelectedHex();
                    break;
                case Keys.S:
                    if (ctrl) ExportXml();
                    break;
                case Keys.E:
                    if (ctrlshft) ExtractAll();
                    else if (ctrl) ExtractRaw();
                    break;
                case Keys.Insert:
                    if (shft) ImportXmlDialog();
                    else if (!ctrl) ImportRaw();
                    break;
                case Keys.C:
                    if (ctrlshft) CopyPath();
                    else if (ctrl) CopySelected();
                    break;
                case Keys.V:
                    if (ctrl) Paste();
                    break;
                case Keys.F2:
                    RenameSelected();
                    break;
                case Keys.Delete:
                    if (shft) DeleteSelected();
                    break;
                case Keys.A:
                    if (ctrl) SelectAll();
                    break;
            }
        }

        private void MainListView_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Control || (e.KeyCode == Keys.ControlKey)) return;
            UpdateSelectionUI(); //need to use this instead of SelectedIndexChanged because of shift-click bug :/
        }

        private void MainListView_ItemActivate(object sender, EventArgs e)
        {
            if (MainListView.SelectedIndices.Count == 1)
            {
                var idx = MainListView.SelectedIndices[0];
                if ((idx >= 0) && (idx < CurrentFiles.Count))
                {
                    var file = CurrentFiles[idx];
                    if (file.Folder != null)
                    {
                        Navigate(file.Folder);
                    }
                    else
                    {
                        //a file was activated. open it... (or, perform "default action"?)
                        View(file);
                    }
                }
            }
            else
            {
                //how to activate multiple items? open files?
            }
        }

        private void MainListView_AfterLabelEdit(object sender, LabelEditEventArgs e)
        {
            if ((CurrentFiles != null) && (CurrentFiles.Count > e.Item) && (!string.IsNullOrEmpty(e.Label)))
            {
                RenameItem(CurrentFiles[e.Item], e.Label);
            }
        }

        private void MainListView_ItemDrag(object sender, ItemDragEventArgs e)
        {
            if (MainListView.SelectedIndices.Count <= 0) return;

            var dir = CreateDropFolder();
            var filenames = new List<string>();
            var errors = new List<string>();

            Cursor = Cursors.WaitCursor;

            var writeFile = new Action<MainListItem, string, bool>((file, outdir, addfilename) => 
            {
                if (file.FileSize > 0x6400000) //100MB
                {
                    errors.Add(file.Name + " is greater than 100MB, drag-drop for large files is disabled.");
                    return;
                }
                try
                {
                    var data = GetFileDataCompressResources(file);
                    var filename = Path.Combine(outdir, file.Name);
                    File.WriteAllBytes(filename, data);
                    if (addfilename)
                    {
                        filenames.Add(filename);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex.ToString());
                }
            });

            for (int i = 0; i < MainListView.SelectedIndices.Count; i++)
            {
                var idx = MainListView.SelectedIndices[i];
                if ((idx < 0) || (idx >= CurrentFiles.Count)) continue;
                var file = CurrentFiles[idx];
                if ((file.Folder == null) || (file.Folder.RpfFile != null))
                {
                    //item is a file (or RPF archive).
                    writeFile(file, dir, true);
                }
                else
                {
                    //item is a folder.
                    var parentpath = file.Parent.Path;
                    var folderstack = new Stack<MainTreeFolder>();
                    folderstack.Push(file.Folder);
                    while (folderstack.Count > 0)
                    {
                        var folder = folderstack.Pop();
                        var folderitems = folder.GetListItems();
                        var relpath = folder.Path.Replace(parentpath, "");
                        var abspath = dir + relpath;
                        if (!Directory.Exists(abspath))
                        {
                            Directory.CreateDirectory(abspath); //create the temp directory...
                        }
                        foreach (var item in folderitems)
                        {
                            if ((item.Folder == null) || (item.Folder.RpfFile != null))
                            {
                                writeFile(item, abspath, false);
                            }
                            else
                            {
                                folderstack.Push(item.Folder);
                            }
                        }
                    }
                    filenames.Add(dir + "\\" + file.Name);
                }
            }

            Cursor = Cursors.Default;

            if (filenames.Count > 0)
            {
                DataObject dataobj = new DataObject(DataFormats.FileDrop, filenames.ToArray());
                DoDragDrop(dataobj, DragDropEffects.Copy);
            }
            else
            {
                if (errors.Count > 0)
                {
                    MessageBox.Show("Errors encountered while dragging:\n" + string.Join("\n", errors.ToArray()));
                }
            }

        }

        private void MainListView_DragEnter(object sender, DragEventArgs e)
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if ((files != null) && (files.Length > 0))
                {
                    if (!files[0].StartsWith(GetDropFolder(), StringComparison.InvariantCultureIgnoreCase)) //don't dry to drop on ourselves...
                    {
                        e.Effect = DragDropEffects.Copy;
                    }
                }
            }
        }

        private void MainListView_DragDrop(object sender, DragEventArgs e)
        {
            if (!EditMode) return;
            if (CurrentFolder?.IsSearchResults ?? false) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if ((files == null) || (files.Length <= 0)) return;
                if (files[0].StartsWith(GetDropFolder(), StringComparison.InvariantCultureIgnoreCase)) return; //don't dry to drop on ourselves...

                if (!EnsureRpfValidEncryption() && (CurrentFolder.RpfFolder != null)) return;

                var filelist = new List<string>();
                var dirdict = new Dictionary<string, RpfDirectoryEntry>();
                ImportFolderStructure(files, filelist, dirdict);
                files = filelist.ToArray();//this will have any directories removed

                //Import as raw regardless of file type while pressing shift
                if ((e.KeyState & 4) == 4)
                {
                    ImportRaw(files, false, dirdict);
                    return;
                }

                var xml = files.Where(x => x.EndsWith(".xml") && (x.IndexOf('.') != x.LastIndexOf('.')));
                var raw = files.Except(xml);

                if (raw.Count() > 0) ImportRaw(raw.ToArray(), false, dirdict);
                if (xml.Count() > 0) ImportXml(xml.ToArray(), false, dirdict);
            }
        }

        private void BackButton_ButtonClick(object sender, EventArgs e)
        {
            GoBack();
        }

        private void BackListButton_Click(object sender, EventArgs e)
        {
            var step = (sender as ToolStripItem)?.Tag as MainTreeFolder;
            if (step == null) return;
            GoBack(step);
        }

        private void ForwardButton_ButtonClick(object sender, EventArgs e)
        {
            GoForward();
        }

        private void ForwardListButton_Click(object sender, EventArgs e)
        {
            var step = (sender as ToolStripItem)?.Tag as MainTreeFolder;
            if (step == null) return;
            GoForward(step);
        }

        private void UpButton_ButtonClick(object sender, EventArgs e)
        {
            GoUp();
        }

        private void UpListButton_Click(object sender, EventArgs e)
        {
            var step = (sender as ToolStripItem)?.Tag as MainTreeFolder;
            if (step == null) return;
            GoUp(step);
        }

        private void LocationTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == 13)
            {
                Navigate(LocationTextBox.Text);
                e.Handled = true;
            }
        }

        private void LocationTextBox_Enter(object sender, EventArgs e)
        {
            BeginInvoke(new Action(() => LocationTextBox.SelectAll()));
        }

        private void GoButton_Click(object sender, EventArgs e)
        {
            Navigate(LocationTextBox.Text);
        }

        private void RefreshButton_Click(object sender, EventArgs e)
        {
            Task.Run(() =>
            {
                RefreshMainTreeView();

                UpdateStatus("Scan complete.");
            });
        }

        private void EditModeButton_Click(object sender, EventArgs e)
        {
            EnableEditMode(!EditMode);
        }

        private void SearchTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == 13)
            {
                SearchButton_ButtonClick(sender, e);
                e.Handled = true;
            }
        }

        private void SearchTextBox_TextChanged(object sender, EventArgs e)
        {
            if (SearchFilterButton.Checked)
            {
                FilterUpdate();
            }
        }

        private void SearchTextBox_Enter(object sender, EventArgs e)
        {
            BeginInvoke(new Action(() => SearchTextBox.SelectAll()));
        }

        private void SearchButton_ButtonClick(object sender, EventArgs e)
        {
            if (SearchGlobalButton.Checked)
            {
                Search(SearchTextBox.Text);
            }
            else
            {
                Filter(SearchTextBox.Text);
            }
        }

        private void SearchGlobalButton_Click(object sender, EventArgs e)
        {
            Search(SearchTextBox.Text);
        }

        private void SearchFilterButton_Click(object sender, EventArgs e)
        {
            Filter(SearchTextBox.Text);
        }

        private void TreeContextCopyPathMenu_Click(object sender, EventArgs e)
        {
            var f = MainTreeView.SelectedNode?.Tag as MainTreeFolder;
            if (f != null)
            {
                CopyPath(f.FullPath);
            }
        }

        private void TreeContextWinExplorerMenu_Click(object sender, EventArgs e)
        {
            var folder = MainTreeView.SelectedNode?.Tag as MainTreeFolder;
            var path = folder?.FullPath ?? GTAFolder.GetCurrentGTAFolderWithTrailingSlash();
            ShowInExplorer(path);
        }

        private void TreeContextExpandMenu_Click(object sender, EventArgs e)
        {
            if (MainTreeView.SelectedNode != null)
            {
                MainTreeView.SelectedNode.Expand();
            }
        }

        private void TreeContextCollapseMenu_Click(object sender, EventArgs e)
        {
            if (MainTreeView.SelectedNode != null)
            {
                MainTreeView.SelectedNode.Collapse();
            }
        }

        private void TreeContextCollapseAllMenu_Click(object sender, EventArgs e)
        {
            var seln = MainTreeView.SelectedNode;
            foreach (TreeNode root in MainTreeView.Nodes)
            {
                foreach (TreeNode n in root.Nodes)
                {
                    n.Collapse(false); //collapse the first level nodes (not the roots)
                }
            }
            if (MainTreeView.SelectedNode != seln)
            {
                TreeViewEventArgs tve = new TreeViewEventArgs(MainTreeView.SelectedNode);
                MainTreeView_AfterSelect(MainTreeView, tve); //for some reason, this event doesn't get raised when the selected node changes here
            }
        }

        private void TreeContextCloseFolderMenu_Click(object sender, EventArgs e)
        {
            var folder = MainTreeView.SelectedNode?.Tag as MainTreeFolder;
            CloseFolder(folder);
        }

        private void ListContextViewMenu_Click(object sender, EventArgs e)
        {
            ViewSelected();
        }

        private void ListContextViewHexMenu_Click(object sender, EventArgs e)
        {
            ViewSelectedHex();
        }

        private void ListContextExportXmlMenu_Click(object sender, EventArgs e)
        {
            ExportXml();
        }

        private void ListContextExtractRawMenu_Click(object sender, EventArgs e)
        {
            ExtractRaw();
        }

        private void ListContextExtractUncompressedMenu_Click(object sender, EventArgs e)
        {
            ExtractUncompressed();
        }

        private void ListContextExtractAllMenu_Click(object sender, EventArgs e)
        {
            ExtractAll();
        }

        private void ListContextNewFolderMenu_Click(object sender, EventArgs e)
        {
            NewFolder();
        }

        private void ListContextNewRpfArchiveMenu_Click(object sender, EventArgs e)
        {
            NewRpfArchive();
        }

        private void ListContextNewYtdFileMenu_Click(object sender, EventArgs e)
        {
            NewYtdFile();
        }

        private void ListContextImportFbxMenu_Click(object sender, EventArgs e)
        {
            ImportFbx();
        }

        private void ListContextImportXmlMenu_Click(object sender, EventArgs e)
        {
            ImportXmlDialog();
        }

        private void ListContextImportRawMenu_Click(object sender, EventArgs e)
        {
            ImportRaw();
        }

        private void ListContextCopyMenu_Click(object sender, EventArgs e)
        {
            CopySelected();
        }

        private void ListContextCopyPathMenu_Click(object sender, EventArgs e)
        {
            CopyPath();
        }

        private void ListContextCopyFileListMenu_Click(object sender, EventArgs e)
        {
            CopyFileList();
        }

        private void ListContextPasteMenu_Click(object sender, EventArgs e)
        {
            Paste();
        }

        private void ListContextOpenFileLocationMenu_Click(object sender, EventArgs e)
        {
            OpenFileLocation();
        }

        private void ListContextRenameMenu_Click(object sender, EventArgs e)
        {
            RenameSelected();
        }

        private void ListContextDeleteMenu_Click(object sender, EventArgs e)
        {
            DeleteSelected();
        }

        private void ListContextDefragmentMenu_Click(object sender, EventArgs e)
        {
            DefragmentSelected();
        }

        private void ListContextSelectAllMenu_Click(object sender, EventArgs e)
        {
            SelectAll();
        }

        private void FileConfigureGameMenu_Click(object sender, EventArgs e)
        {
            ConfigureGame();
        }

        private void FileOpenFolderMenu_Click(object sender, EventArgs e)
        {
            OpenFolder();
        }

        private void FileExitMenu_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void EditViewMenu_Click(object sender, EventArgs e)
        {
            ViewSelected();
        }

        private void EditViewHexMenu_Click(object sender, EventArgs e)
        {
            ViewSelectedHex();
        }

        private void EditExportXmlMenu_Click(object sender, EventArgs e)
        {
            ExportXml();
        }

        private void EditExtractRawMenu_Click(object sender, EventArgs e)
        {
            ExtractRaw();
        }

        private void EditExtractAllMenu_Click(object sender, EventArgs e)
        {
            ExtractAll();
        }

        private void EditImportFbxMenu_Click(object sender, EventArgs e)
        {
            ImportFbx();
        }

        private void EditImportXmlMenu_Click(object sender, EventArgs e)
        {
            ImportXmlDialog();
        }

        private void EditImportRawMenu_Click(object sender, EventArgs e)
        {
            ImportRaw();
        }

        private void EditCopyMenu_Click(object sender, EventArgs e)
        {
            CopySelected();
        }

        private void EditCopyPathMenu_Click(object sender, EventArgs e)
        {
            CopyPath();
        }

        private void EditCopyFileListMenu_Click(object sender, EventArgs e)
        {
            CopyFileList();
        }

        private void EditPasteMenu_Click(object sender, EventArgs e)
        {
            Paste();
        }

        private void EditRenameMenu_Click(object sender, EventArgs e)
        {
            RenameSelected();
        }

        private void EditDeleteMenu_Click(object sender, EventArgs e)
        {
            DeleteSelected();
        }

        private void EditSelectAllMenu_Click(object sender, EventArgs e)
        {
            SelectAll();
        }

        private void ViewLargeIconsMenu_Click(object sender, EventArgs e)
        {
            SetView(System.Windows.Forms.View.LargeIcon);
        }

        private void ViewSmallIconsMenu_Click(object sender, EventArgs e)
        {
            SetView(System.Windows.Forms.View.SmallIcon);
        }

        private void ViewListMenu_Click(object sender, EventArgs e)
        {
            SetView(System.Windows.Forms.View.List);
        }

        private void ViewDetailsMenu_Click(object sender, EventArgs e)
        {
            SetView(System.Windows.Forms.View.Details);
        }

        private void ViewThemeWindowsMenu_Click(object sender, EventArgs e)
        {
            SetTheme("Windows");
        }

        private void ViewThemeBlueMenu_Click(object sender, EventArgs e)
        {
            SetTheme("Blue");
        }

        private void ViewThemeLightMenu_Click(object sender, EventArgs e)
        {
            SetTheme("Light");
        }

        private void ViewThemeDarkMenu_Click(object sender, EventArgs e)
        {
            SetTheme("Dark");
        }

        private void ToolsRpfBrowserMenu_Click(object sender, EventArgs e)
        {
            BrowseForm f = new BrowseForm();
            f.Show(this);
        }

        private void ToolsBinSearchMenu_Click(object sender, EventArgs e)
        {
            BinarySearchForm f = new BinarySearchForm(GetFileCache());
            f.Show(this);
        }

        private void ToolsAudioExplorerMenu_Click(object sender, EventArgs e)
        {
            AudioExplorerForm f = new AudioExplorerForm(GetFileCache());
            f.Show(this);
        }

        private void ToolsJenkGenMenu_Click(object sender, EventArgs e)
        {
            JenkGenForm f = new JenkGenForm();
            f.Show(this);
        }

        private void ToolsJenkIndMenu_Click(object sender, EventArgs e)
        {
            JenkIndForm f = new JenkIndForm(GetFileCache());
            f.Show(this);
        }

        private void ToolsAssetConverterMenu_Click(object sender, EventArgs e)
        {
            ConvertAssetsForm f = new ConvertAssetsForm();
            f.Show(this);
        }

        private void OptionsStartInEditModeMenu_Click(object sender, EventArgs e)
        {
            OptionsStartInEditModeMenu.Checked = !OptionsStartInEditModeMenu.Checked;
            Settings.Default.RPFExplorerStartInEditMode = OptionsStartInEditModeMenu.Checked;
        }

        private void OptionsStartInFolderDefaultMenu_Click(object sender, EventArgs e)
        {
            Settings.Default.RPFExplorerStartFolder = string.Empty;
            OptionsStartInFolderValueMenu.Text = "(Default)";
        }

        private void OptionsStartInFolderCurrentMenu_Click(object sender, EventArgs e)
        {
            if (CurrentFolder == null) return;
            Settings.Default.RPFExplorerStartFolder = CurrentFolder.Path;
            OptionsStartInFolderValueMenu.Text = string.IsNullOrEmpty(CurrentFolder.Path) ? "(Default)" : CurrentFolder.Path;
        }

        private void CopyToModsFolderButton_Click(object sender, EventArgs e)
        {
            CopyToModsFolder();
        }
    }



    public class MainTreeFolder
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string FullPath { get; set; }
        public RpfFile RpfFile { get; set; }
        public RpfDirectoryEntry RpfFolder { get; set; }
        public List<string> Files { get; set; }
        public MainTreeFolder Parent { get; set; }
        public List<MainTreeFolder> Children { get; set; }
        public List<MainListItem> ListItems { get; set; }
        public TreeNode TreeNode { get; set; }
        public bool IsSearchResults { get; set; }
        public string SearchTerm { get; set; }
        public bool IsExtraFolder { get; set; }

        public void AddFile(string file)
        {
            if (Files == null) Files = new List<string>();
            Files.Add(file);
        }
        public void AddChild(MainTreeFolder child)
        {
            if (Children == null) Children = new List<MainTreeFolder>();
            Children.Add(child);
            child.Parent = this;
        }
        public void AddChildToHierarchy(MainTreeFolder child)
        {
            var relpath = child.Path.Replace(Path + "\\", "");
            var idx = relpath.IndexOf('\\');
            var lidx = 0;
            var parent = this;
            while (idx > 0)
            {
                var pname = relpath.Substring(lidx, idx - lidx);
                if (parent.Children == null) break;
                foreach (var tc in parent.Children)
                {
                    if (tc.Name.Equals(pname, StringComparison.InvariantCultureIgnoreCase))
                    {
                        parent = tc;
                        break;
                    }
                }
                lidx = idx + 1;
                if (lidx >= relpath.Length) break;
                idx = relpath.IndexOf('\\', lidx);
            }
            parent.AddChild(child);
        }
        public void RemoveChild(MainTreeFolder child)
        {
            if (Children == null) return;
            Children.Remove(child);
        }

        public void EnsureFile(string file)
        {
            if (Files != null)
            {
                if (Files.Contains(file)) return;
            }
            AddFile(file);
        }
        public void RemoveFile(string file)
        {
            if (Files != null)
            {
                Files.Remove(file);
            }
        }

        public List<MainListItem> GetListItems()
        {
            if (ListItems == null)
            {
                ListItems = new List<MainListItem>();
                var rootpath = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();

                if (Children != null)
                {
                    foreach (var child in Children)
                    {
                        ListItems.Add(new MainListItem(child));
                    }
                }
                if (Files != null)
                {
                    foreach (var file in Files)
                    {
                        ListItems.Add(new MainListItem(file, rootpath, this));
                    }
                }
                if ((RpfFolder != null) && (RpfFolder.Files != null))
                {
                    foreach (var file in RpfFolder.Files)
                    {
                        if (file.NameLower.EndsWith(".rpf")) continue; //RPF files are already added..
                        ListItems.Add(new MainListItem(file, rootpath, this));
                    }
                }
            }
            return ListItems;
        }
        public int GetItemCount()
        {
            int ic = 0;
            if (Children != null) ic += Children.Count;
            if (Files != null) ic += Files.Count;
            if ((RpfFolder != null) && (RpfFolder.Files != null))
            {
                foreach (var file in RpfFolder.Files)
                {
                    if (file.NameLower.EndsWith(".rpf")) continue; //RPF files are already added..
                    ic++;
                }
            }
            return ic;
        }

        public string GetToolText()
        {
            if (string.IsNullOrEmpty(Path)) return Name;
            return Path;
        }

        public int Search(string[] terms, ExploreForm form)
        {
            int resultcount = 0;
            //if (!form.Searching) return resultcount;

            form.UpdateStatus("Searching " + Path + "...");

            if (SearchMatch(Name.ToLowerInvariant(), terms))
            {
                form.AddSearchResult(new MainListItem(this));
                resultcount++;
            }

            var rootpath = GTAFolder.GetCurrentGTAFolderWithTrailingSlash();

            if (Files != null)
            {
                foreach (var file in Files)
                {
                    //if (!form.Searching) return resultcount;
                    var fi = new FileInfo(file);
                    if (SearchMatch(fi.Name.ToLowerInvariant(), terms))
                    {
                        form.AddSearchResult(new MainListItem(file, rootpath, this));
                        resultcount++;
                    }
                }
            }
            if ((RpfFolder != null) && (RpfFolder.Files != null))
            {
                foreach (var file in RpfFolder.Files)
                {
                    //if (!form.Searching) return resultcount;
                    if (file.NameLower.EndsWith(".rpf")) continue; //don't search rpf files..
                    if (SearchMatch(file.NameLower, terms))
                    {
                        form.AddSearchResult(new MainListItem(file, rootpath, this));
                        resultcount++;
                    }
                }
            }

            if (Children != null)
            {
                foreach (var child in Children)
                {
                    //if (!form.Searching) return resultcount;
                    resultcount += child.Search(terms, form);
                }
            }

            form.AddSearchResult(null);

            return resultcount;
        }
        public static bool SearchMatch(string test, string[] terms)
        {
            int startidx = 0;
            for (int i = 0; i < terms.Length; i++)
            {
                if (startidx >= test.Length) return false;
                var term = terms[i];
                var idx = test.IndexOf(term, startidx);
                if (idx < 0) return false;
                startidx = idx + term.Length;
            }
            return true;
        }

        public void Clear()
        {
            RpfFile = null;
            RpfFolder = null;
            Files = null;
            Parent = null;
            Children = null;
            ListItems = null;
            TreeNode = null;
        }

        public MainTreeFolder FindChild(string name)
        {
            if (Children == null) return null;
            foreach (var child in Children)
            {
                if (child == null) continue;
                if (child.Name == null) continue;
                if (child.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase))
                {
                    return child;
                }
            }
            return null;
        }
        public RpfFile GetCurrentRpfFile()
        {
            if (RpfFile != null) return RpfFile;
            return Parent?.GetCurrentRpfFile();
        }

        public override string ToString()
        {
            return Name;
        }
    }

    public class MainListItem
    {
        public string Name { get; set; }
        public MainTreeFolder Parent { get; set; }
        public MainTreeFolder Folder { get; set; }
        public RpfFileEntry File { get; set; }
        public string Path { get; set; }
        public string FullPath { get; set; }

        public FileTypeInfo FileType { get; set; }
        public string FileTypeText { get; set; }
        public long FileSize { get; set; }
        public string FileSizeText { get; set; }
        public string Attributes { get; set; }
        public int ImageIndex { get; set; }


        public MainListItem(MainTreeFolder f)
        {
            Parent = f?.Parent;
            Folder = f;
            Name = f.Name;
            Path = f.Path;
            FullPath = f.FullPath;
        }
        public MainListItem(string file, string rootpath, MainTreeFolder parent)
        {
            Parent = parent;
            Name = new FileInfo(file).Name;
            Path = file.Replace(rootpath, "");
            FullPath = file;
        }
        public MainListItem(RpfFileEntry file, string rootpath, MainTreeFolder parent)
        {
            Parent = parent;
            File = file;
            Name = file.Name;
            Path = file.Path;
            FullPath = rootpath + file.Path;
        }

        public override string ToString()
        {
            return Name;
        }

        public void CacheDetails(ExploreForm form)
        {
            var fld = Folder;
            FileType = form.GetFileType(Name);
            FileTypeText = FileType.Name;
            FileSizeText = "";
            Attributes = "";
            ImageIndex = FileType.ImageIndex;
            if (File != null)
            {
                FileSize = File.GetFileSize();
                FileSizeText = TextUtil.GetBytesReadable(FileSize);
                if (File is RpfResourceFileEntry)
                {
                    var resf = File as RpfResourceFileEntry;
                    Attributes += "Resource [V." + resf.Version.ToString() + "]";
                }
                if (File.IsEncrypted)
                {
                    if (Attributes.Length > 0) Attributes += ", ";
                    Attributes += "Encrypted";
                }
            }
            else if (fld != null)
            {
                if (fld.RpfFile != null)
                {
                    FileSize = fld.RpfFile.FileSize;
                    FileSizeText = TextUtil.GetBytesReadable(FileSize);
                    Attributes += fld.RpfFile.Encryption.ToString() + " encryption";
                }
                else
                {
                    FileTypeText = "Folder";
                    ImageIndex = 1; //FOLDER imageIndex
                    var ic = fld.GetItemCount();
                    FileSize = ic;
                    FileSizeText = ic.ToString() + " item" + ((ic != 1) ? "s" : "");
                }
            }
            else
            {
                var fi = new FileInfo(FullPath);
                if (fi.Exists)
                {
                    FileSize = fi.Length;
                    FileSizeText = TextUtil.GetBytesReadable(fi.Length);
                }
            }

        }

        public int SortCompare(MainListItem i, int col, SortOrder dir)
        {
            var desc = (dir == SortOrder.Descending);
            if (Folder != null)
            {
                if (i.Folder == null) return desc ? 1 : -1;
            }
            else if (i.Folder != null)
            {
                return desc ? -1 : 1;
            }

            var i1 = this;
            var i2 = i;
            if (desc)
            {
                i1 = i;
                i2 = this;
            }

            switch (col)
            {
                default:
                case 0: //Name column
                    return i1.Name.CompareTo(i2.Name);
                case 1: //Type column
                    var ftc = i1.FileTypeText.CompareTo(i2.FileTypeText);
                    if (ftc == 0) return i1.Name.CompareTo(i2.Name); //same type, sort by name...
                    return ftc;
                case 2: //Size column
                    return i1.FileSize.CompareTo(i2.FileSize);
                case 3: //Attributes column
                    var ac = i1.Attributes.CompareTo(i2.Attributes);
                    if (ac == 0) return i1.Name.CompareTo(i2.Name); //same attributes, sort by name...
                    return ac;
                case 4: //path column
                    return i1.Path.CompareTo(i2.Path);
            }

            //return i1.Name.CompareTo(i2.Name);
        }

        public RpfEntry GetRpfEntry()
        {
            RpfFile file = Folder?.RpfFile;
            RpfDirectoryEntry fldr = Folder?.RpfFolder;
            RpfEntry entry = File;
            if (entry == null)
            {
                if (file != null)
                {
                    //for an RPF file, get its entry in the parent (if any).
                    entry = file.ParentFileEntry;
                }
                else if (fldr != null)
                {
                    //RPF folders are referenced in the item.Folder
                    entry = fldr;
                }
            }
            return entry;
        }

    }


    public class FileTypeInfo
    {
        public string Name { get; set; }
        public string Extension { get; set; }
        public int ImageIndex { get; set; }
        public FileTypeAction DefaultAction { get; set; }
        public List<FileTypeInfo> SubTypes { get; set; }
        public bool XmlConvertible { get; set; }

        public FileTypeInfo(string extension, string name, int imageindex, FileTypeAction defaultAction, bool xmlConvertible)
        {
            Name = name;
            Extension = extension;
            ImageIndex = imageindex;
            DefaultAction = defaultAction;
            XmlConvertible = xmlConvertible;
        }

        public void AddSubType(FileTypeInfo t)
        {
            if (SubTypes == null) SubTypes = new List<FileTypeInfo>();
            SubTypes.Add(t);
        }
    }

    public enum FileTypeAction
    {
        ViewHex = 0,
        ViewText = 1,
        ViewXml = 2,
        ViewYtd = 3,
        ViewYmt = 4,
        ViewYmf = 5,
        ViewYmap = 6,
        ViewYtyp = 7,
        ViewJPso = 8,
        ViewModel = 9,
        ViewCut = 10,
        ViewAwc = 11,
        ViewGxt = 12,
        ViewRel = 13,
        ViewFxc = 14,
        ViewYwr = 15,
        ViewYvr = 16,
        ViewYcd = 17,
        ViewYnd = 18,
        ViewCacheDat = 19,
        ViewYed = 20,
        ViewYld = 21,
        ViewYfd = 22,
        ViewHeightmap = 23,
        ViewMrf = 24,
        ViewNametable = 25,
        ViewDistantLights = 26,
        ViewYpdb = 27,
    }









}
