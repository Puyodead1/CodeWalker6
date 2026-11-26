using CodeWalker.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodeWalker.Utils
{
    public partial class SelectFolderForm : Form
    {

        public string SelectedFolder { get; set; }
        public bool IsGen9 { get; set; }
        public bool IsGta6 { get; set; }
        /// <summary>GTA VI: the game exe or gta6_keys.dat to take the keys from; empty = automatic</summary>
        public string Gta6Keys { get; set; }
        public DialogResult Result { get; set; } = DialogResult.Cancel;

        public SelectFolderForm()
        {
            InitializeComponent();
            SelectedFolder = GTAFolder.CurrentGTAFolder;
            IsGen9 = GTAFolder.IsGen9;
            IsGta6 = GTAFolder.IsGta6;
            Gta6Keys = GTAFolder.Gta6Keys;
        }

        private void SelectFolderForm_Load(object sender, EventArgs e)
        {
            FolderTextBox.Text = SelectedFolder;
            KeysTextBox.Text = Gta6Keys;
            RememberFolderCheckbox.Checked = Settings.Default.RememberGTAFolder;
            SetGame(IsGen9, IsGta6);
        }

        private void SetGame(bool gen9, bool gta6)
        {
            LegacyRadioButton.Checked = !gen9 && !gta6;
            EnhancedRadioButton.Checked = gen9 && !gta6;
            Gta6RadioButton.Checked = gta6;
            UpdateKeysRow();
        }

        private void UpdateKeysRow()
        {
            KeysLabel.Enabled = KeysTextBox.Enabled = KeysBrowseButton.Enabled = KeysHintLabel.Enabled = Gta6RadioButton.Checked;
        }

        private void FolderBrowseButton_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog.SelectedPath = FolderTextBox.Text;
            DialogResult res = FolderBrowserDialog.ShowDialogNew();
            if (res == DialogResult.OK)
            {
                var path = FolderBrowserDialog.SelectedPath;
                FolderTextBox.Text = path;
                SetGame(GTAFolder.IsGen9Folder(path), GTAFolder.IsGta6Folder(path));
            }
        }

        private void FolderTextBox_TextChanged(object sender, EventArgs e)
        {
            SelectedFolder = FolderTextBox.Text;
        }

        private void GameRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            IsGen9 = EnhancedRadioButton.Checked;
            IsGta6 = Gta6RadioButton.Checked;
            UpdateKeysRow();
        }

        private void KeysTextBox_TextChanged(object sender, EventArgs e)
        {
            Gta6Keys = KeysTextBox.Text.Trim().Trim('"');
        }

        private void KeysBrowseButton_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "GTA VI keys: the game exe (game_scarlett_master.exe) or a key file (gta6_keys.dat)";
                ofd.Filter = "Game exe or key file (*.exe;*.dat)|*.exe;*.dat|All files (*.*)|*.*";
                if (File.Exists(KeysTextBox.Text)) ofd.FileName = KeysTextBox.Text;
                if (ofd.ShowDialog(this) == DialogResult.OK) KeysTextBox.Text = ofd.FileName;
            }
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            if(!GTAFolder.ValidateGTAFolder(SelectedFolder, IsGen9, IsGta6, out string failReason))
            {
                MessageBox.Show("The selected folder could not be used:\n\n" + failReason, "Invalid GTA Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (IsGta6 && !string.IsNullOrEmpty(Gta6Keys) && !File.Exists(Gta6Keys))
            {
                MessageBox.Show("GTA VI keys file not found:\n\n" + Gta6Keys, "Invalid GTA VI keys", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Result = DialogResult.OK;
            Close();
        }

        private void RememberFolderCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            Settings.Default.RememberGTAFolder = RememberFolderCheckbox.Checked;
        }
    }
}
