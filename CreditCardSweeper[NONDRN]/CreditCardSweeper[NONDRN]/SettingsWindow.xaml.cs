using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms ; 
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CreditCardSweeper
{
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            SLT_Text.Text = ConfigurationManager.AppSettings.Get("SaveLogsTo");
            DS_Text.Text = ConfigurationManager.AppSettings.Get("DefaultServer");
            AS_Chk.IsChecked = Convert.ToBoolean(ConfigurationManager.AppSettings.Get("AlwaysSave"));
            AM_Chk.IsChecked = Convert.ToBoolean(ConfigurationManager.AppSettings.Get("AlwaysMask")); 
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool AlwaysSave = AS_Chk.IsChecked ?? false;
                bool AlwaysMask = AM_Chk.IsChecked ?? false;
                ConfigurationManager.AppSettings.Set("SaveLogsTo", SLT_Text.Text);
                ConfigurationManager.AppSettings.Set("DefaultServer", DS_Text.Text);
                ConfigurationManager.AppSettings.Set("AlwaysSave", AlwaysSave.ToString());
                ConfigurationManager.AppSettings.Set("AlwaysMask", AlwaysMask.ToString());
                ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).Save(ConfigurationSaveMode.Modified, true);
                ConfigurationManager.RefreshSection("appSettings");
                System.Windows.MessageBox.Show(this, "Settings successfully saved.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (ConfigurationErrorsException)
            {
                System.Windows.MessageBox.Show(this, "Error occured while trying to save settings.", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.DialogResult = false; 
            }

            this.DialogResult = true; 
        }

        private void SLGDirectoryBtn_Click(object sender, RoutedEventArgs e)
        {
            FolderBrowserDialog DirectoryDialog = new FolderBrowserDialog();
            DirectoryDialog.Description = "Please select a location to save error and scan log files to.";
            DirectoryDialog.ShowNewFolderButton = true;

            if (DirectoryDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SLT_Text.Text = DirectoryDialog.SelectedPath;
            } 
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }
    }
}
