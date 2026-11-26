namespace CodeWalker.Utils
{
    partial class SelectFolderForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(SelectFolderForm));
            this.OkButton = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.FolderBrowseButton = new System.Windows.Forms.Button();
            this.FolderTextBox = new System.Windows.Forms.TextBox();
            this.FolderBrowserDialog = new System.Windows.Forms.FolderBrowserDialog();
            this.CancelButt = new System.Windows.Forms.Button();
            this.RememberFolderCheckbox = new System.Windows.Forms.CheckBox();
            this.LegacyRadioButton = new System.Windows.Forms.RadioButton();
            this.EnhancedRadioButton = new System.Windows.Forms.RadioButton();
            this.Gta6RadioButton = new System.Windows.Forms.RadioButton();
            this.KeysLabel = new System.Windows.Forms.Label();
            this.KeysTextBox = new System.Windows.Forms.TextBox();
            this.KeysBrowseButton = new System.Windows.Forms.Button();
            this.KeysHintLabel = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // OkButton
            // 
            this.OkButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.OkButton.Location = new System.Drawing.Point(319, 188);
            this.OkButton.Name = "OkButton";
            this.OkButton.Size = new System.Drawing.Size(75, 23);
            this.OkButton.TabIndex = 64;
            this.OkButton.Text = "Ok";
            this.OkButton.UseVisualStyleBackColor = true;
            this.OkButton.Click += new System.EventHandler(this.OkButton_Click);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(12, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(158, 13);
            this.label1.TabIndex = 63;
            this.label1.Text = "Please select your game folder:";
            // 
            // FolderBrowseButton
            // 
            this.FolderBrowseButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.FolderBrowseButton.Location = new System.Drawing.Point(367, 35);
            this.FolderBrowseButton.Name = "FolderBrowseButton";
            this.FolderBrowseButton.Size = new System.Drawing.Size(27, 23);
            this.FolderBrowseButton.TabIndex = 62;
            this.FolderBrowseButton.Text = "...";
            this.FolderBrowseButton.UseVisualStyleBackColor = true;
            this.FolderBrowseButton.Click += new System.EventHandler(this.FolderBrowseButton_Click);
            // 
            // FolderTextBox
            // 
            this.FolderTextBox.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.FolderTextBox.Location = new System.Drawing.Point(12, 36);
            this.FolderTextBox.Name = "FolderTextBox";
            this.FolderTextBox.Size = new System.Drawing.Size(345, 20);
            this.FolderTextBox.TabIndex = 61;
            this.FolderTextBox.TextChanged += new System.EventHandler(this.FolderTextBox_TextChanged);
            // 
            // CancelButt
            // 
            this.CancelButt.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.CancelButt.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.CancelButt.Location = new System.Drawing.Point(238, 188);
            this.CancelButt.Name = "CancelButt";
            this.CancelButt.Size = new System.Drawing.Size(75, 23);
            this.CancelButt.TabIndex = 65;
            this.CancelButt.Text = "Cancel";
            this.CancelButt.UseVisualStyleBackColor = true;
            this.CancelButt.Click += new System.EventHandler(this.CancelButton_Click);
            // 
            // RememberFolderCheckbox
            // 
            this.RememberFolderCheckbox.AutoSize = true;
            this.RememberFolderCheckbox.Location = new System.Drawing.Point(12, 192);
            this.RememberFolderCheckbox.Name = "RememberFolderCheckbox";
            this.RememberFolderCheckbox.Size = new System.Drawing.Size(118, 17);
            this.RememberFolderCheckbox.TabIndex = 66;
            this.RememberFolderCheckbox.Text = "Remember Settings";
            this.RememberFolderCheckbox.UseVisualStyleBackColor = true;
            this.RememberFolderCheckbox.CheckedChanged += new System.EventHandler(this.RememberFolderCheckbox_CheckedChanged);
            // 
            // LegacyRadioButton
            // 
            this.LegacyRadioButton.AutoSize = true;
            this.LegacyRadioButton.Checked = true;
            this.LegacyRadioButton.Location = new System.Drawing.Point(15, 68);
            this.LegacyRadioButton.Name = "LegacyRadioButton";
            this.LegacyRadioButton.Size = new System.Drawing.Size(92, 17);
            this.LegacyRadioButton.TabIndex = 67;
            this.LegacyRadioButton.TabStop = true;
            this.LegacyRadioButton.Text = "GTAV Legacy";
            this.LegacyRadioButton.UseVisualStyleBackColor = true;
            this.LegacyRadioButton.CheckedChanged += new System.EventHandler(this.GameRadioButton_CheckedChanged);
            //
            // EnhancedRadioButton
            // 
            this.EnhancedRadioButton.AutoSize = true;
            this.EnhancedRadioButton.Location = new System.Drawing.Point(15, 91);
            this.EnhancedRadioButton.Name = "EnhancedRadioButton";
            this.EnhancedRadioButton.Size = new System.Drawing.Size(106, 17);
            this.EnhancedRadioButton.TabIndex = 68;
            this.EnhancedRadioButton.Text = "GTAV Enhanced";
            this.EnhancedRadioButton.UseVisualStyleBackColor = true;
            this.EnhancedRadioButton.CheckedChanged += new System.EventHandler(this.GameRadioButton_CheckedChanged);
            //
            // Gta6RadioButton
            //
            this.Gta6RadioButton.AutoSize = true;
            this.Gta6RadioButton.Location = new System.Drawing.Point(15, 114);
            this.Gta6RadioButton.Name = "Gta6RadioButton";
            this.Gta6RadioButton.Size = new System.Drawing.Size(58, 17);
            this.Gta6RadioButton.TabIndex = 69;
            this.Gta6RadioButton.Text = "GTA VI";
            this.Gta6RadioButton.UseVisualStyleBackColor = true;
            this.Gta6RadioButton.CheckedChanged += new System.EventHandler(this.GameRadioButton_CheckedChanged);
            //
            // KeysLabel
            //
            this.KeysLabel.AutoSize = true;
            this.KeysLabel.Location = new System.Drawing.Point(32, 140);
            this.KeysLabel.Name = "KeysLabel";
            this.KeysLabel.Size = new System.Drawing.Size(56, 13);
            this.KeysLabel.TabIndex = 70;
            this.KeysLabel.Text = "Keys from:";
            //
            // KeysTextBox
            //
            this.KeysTextBox.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.KeysTextBox.Location = new System.Drawing.Point(94, 137);
            this.KeysTextBox.Name = "KeysTextBox";
            this.KeysTextBox.Size = new System.Drawing.Size(263, 20);
            this.KeysTextBox.TabIndex = 71;
            this.KeysTextBox.TextChanged += new System.EventHandler(this.KeysTextBox_TextChanged);
            //
            // KeysBrowseButton
            //
            this.KeysBrowseButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.KeysBrowseButton.Location = new System.Drawing.Point(367, 136);
            this.KeysBrowseButton.Name = "KeysBrowseButton";
            this.KeysBrowseButton.Size = new System.Drawing.Size(27, 23);
            this.KeysBrowseButton.TabIndex = 72;
            this.KeysBrowseButton.Text = "...";
            this.KeysBrowseButton.UseVisualStyleBackColor = true;
            this.KeysBrowseButton.Click += new System.EventHandler(this.KeysBrowseButton_Click);
            //
            // KeysHintLabel
            //
            this.KeysHintLabel.AutoSize = true;
            this.KeysHintLabel.ForeColor = System.Drawing.SystemColors.GrayText;
            this.KeysHintLabel.Location = new System.Drawing.Point(91, 161);
            this.KeysHintLabel.Name = "KeysHintLabel";
            this.KeysHintLabel.Size = new System.Drawing.Size(300, 13);
            this.KeysHintLabel.TabIndex = 73;
            this.KeysHintLabel.Text = "game exe or gta6_keys.dat (blank = automatic)";
            //
            // SelectFolderForm
            //
            this.AcceptButton = this.OkButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.CancelButt;
            this.ClientSize = new System.Drawing.Size(406, 223);
            this.Controls.Add(this.KeysHintLabel);
            this.Controls.Add(this.KeysBrowseButton);
            this.Controls.Add(this.KeysTextBox);
            this.Controls.Add(this.KeysLabel);
            this.Controls.Add(this.Gta6RadioButton);
            this.Controls.Add(this.EnhancedRadioButton);
            this.Controls.Add(this.LegacyRadioButton);
            this.Controls.Add(this.RememberFolderCheckbox);
            this.Controls.Add(this.CancelButt);
            this.Controls.Add(this.OkButton);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.FolderBrowseButton);
            this.Controls.Add(this.FolderTextBox);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MinimumSize = new System.Drawing.Size(422, 152);
            this.Name = "SelectFolderForm";
            this.Text = "Select game folder - CodeWalker by dexyfex";
            this.Load += new System.EventHandler(this.SelectFolderForm_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button OkButton;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button FolderBrowseButton;
        private System.Windows.Forms.TextBox FolderTextBox;
        private System.Windows.Forms.FolderBrowserDialog FolderBrowserDialog;
        private System.Windows.Forms.Button CancelButt;
        private System.Windows.Forms.CheckBox RememberFolderCheckbox;
        private System.Windows.Forms.RadioButton LegacyRadioButton;
        private System.Windows.Forms.RadioButton EnhancedRadioButton;
        private System.Windows.Forms.RadioButton Gta6RadioButton;
        private System.Windows.Forms.Label KeysLabel;
        private System.Windows.Forms.TextBox KeysTextBox;
        private System.Windows.Forms.Button KeysBrowseButton;
        private System.Windows.Forms.Label KeysHintLabel;
    }
}