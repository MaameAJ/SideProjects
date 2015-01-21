using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration; 
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Threading;

namespace CreditCardSweeper
{
    public enum ScanStatus { Initializing, Scanning, Error, Complete };
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private ObservableCollection<ProcessInfo> Processes = new ObservableCollection<ProcessInfo>();
        private ObservableCollection<String> Logs = new ObservableCollection<string>();

        public MainWindow()
        {
            InitializeComponent();

            ScanList.ItemsSource = Processes;
            FilesCMB.ItemsSource = Logs; 
        }

        private void NewScanBTN_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Scan nScan = new Scan(serverTxt.Text.Trim(), databaseTxt.Text.Trim(), ConfigurationManager.AppSettings.Get("SaveLogsTo"),
                    SaveLogsChk.IsChecked ?? false, MaskDataChk.IsChecked ?? false);
                nScan.ScanEventOccured = ScanEvent_Occured;
                nScan.DataEventOccured = DataEvent_Occured;

                ProcessInfo process = new ProcessInfo(nScan);
                process.Status = ScanStatus.Initializing;
                process.Message = "Initializing";
                Processes.Add(process);

                Thread processThread = new Thread(this.StartNewScan);
                processThread.IsBackground = true;
                processThread.Start(nScan);
            }
            catch (ArgumentException a)
            {
                MessageBoxResult mresult = MessageBox.Show(this, a.Message, "Failed to create scan", MessageBoxButton.OKCancel, MessageBoxImage.Error, MessageBoxResult.OK);
            }
        }

        private void StartNewScan(object newCCScan)
        {
            Scan nCCScan = (Scan) newCCScan; 
            try
            {
                nCCScan.Sweep();
            }
            catch (Exception e)
            {
                this.Dispatcher.Invoke((Action)delegate
                {
                    int index = Processes.IndexOf(nCCScan);
                    if (index >= 0)
                    {
                        ProcessInfo process = Processes[index];
                        process.Status = ScanStatus.Error;
                        process.Message = "Scan failed due to " + e.GetType().Name + ": " + e.Message;
                        Processes[index] = process;
                    }
                });
                //if(e is ArgumentException || e is System.Data.SqlClient.SqlException || 
            }
        }

        #region Scan Event Handlers

        private void ScanEvent_Occured(object sender, ScanEventArgs e)
        {
            this.Dispatcher.Invoke((Action)delegate
            {
                int index = Processes.IndexOf((Scan)sender);
                if (index >= 0)
                {
                    ProcessInfo process = Processes[index];
                    process.Server = e.Server;
                    process.Database = e.Database;
                    process.Message = e.message;
                    if (e.Type == EventType.ScanCompleted)
                    {
                        process.Status = ScanStatus.Complete;
                        process.CCHits = e.hits;
                        process.CCMasked = e.masked;

                        Scan pScan = process.CCScan;
                        Logs.Add(pScan.ErrorLog);
                        Logs.Add(GetScanLogDisplayName(pScan));
                    }
                    else if (e.Type == EventType.ScanStart)
                    {
                        process.Status = ScanStatus.Scanning;
                    }

                    Processes[index] = process;
                }
            });
        }

        private void DataEvent_Occured(object sender, DataEventArgs e)
        {
            this.Dispatcher.Invoke((Action)delegate
            {
                int index = Processes.IndexOf((Scan)sender);
                if (index >= 0)
                {
                    ProcessInfo process = Processes[index];
                    process.Server = e.Server;
                    process.Database = e.Database;
                    process.Table = e.Table;
                    process.Message = e.message;
                    if (e.Type != EventType.TableScanStart)
                    {
                        process.Column = e.Column;
                        if (e.Type != EventType.ColumnScanStart)
                        {
                            process.CCHits += e.hits;
                            if (e.Type == EventType.CCDataMasked) process.CCMasked += e.masked;
                        }
                    }

                    Processes[index] = process;
                }
            });
        }

        #endregion

        private void SettingsMI_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow settings = new SettingsWindow();
            settings.Owner = this; 
            settings.ShowDialog(); 
        }

        private void ClearBTN_Click(object sender, RoutedEventArgs e)
        {
            List<ProcessInfo> ToRemove = Processes.Where(p => (p.Status == ScanStatus.Complete)).ToList<ProcessInfo>();
            foreach (var p in ToRemove)
            {
                Logs.Remove(GetScanLogDisplayName(p.CCScan)); 
                Logs.Remove(p.CCScan.ErrorLog); 
                Processes.Remove(p);
            }
        }

        private string GetScanLogDisplayName(Scan cscan)
        {
            if(cscan.IsSavingScanLogs) return cscan.ScanLog;
            else return "temp" + "_" + cscan.GenerateFileName(false); 
        }

        private string GetActualScanLog(string displayFileName)
        {
            if(displayFileName.Contains("temp"))
            {
                for(int i = 0; i < Processes.Count; i++)
                {
                    Scan cScan = Processes[i].CCScan;
                    if(GetScanLogDisplayName(cScan) == displayFileName) return cScan.ScanLog; 
                }
                return String.Empty; 
            }
            else return displayFileName; 
        }

        private void FilesCMB_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilesCMB.SelectedItem == null) return;
            string sFile = FilesCMB.SelectedItem.ToString();
            string file = GetActualScanLog(sFile);
            if (System.IO.File.Exists(file))
            {
                Paragraph pgraph = new Paragraph();
                pgraph.Inlines.Add(System.IO.File.ReadAllText(file));
                LogsDocViewer.Document = new FlowDocument(pgraph); 
            }
            else
            {
                MessageBox.Show(this, "Specified File does not appear to exist. It will now be deleted from drop-down.", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                Logs.Remove(sFile); 
            }
        }

        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.OpenFileDialog findDLG = new System.Windows.Forms.OpenFileDialog();
            findDLG.Multiselect = false;
            findDLG.Title = "Browse for Logfile to View";
            findDLG.InitialDirectory = ConfigurationManager.AppSettings.Get("SaveToLogs");
            findDLG.AutoUpgradeEnabled = true;
            findDLG.CheckFileExists = findDLG.CheckPathExists = true;
            findDLG.DefaultExt = ".log";
            if (findDLG.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                Logs.Add(findDLG.FileName);
                FilesCMB.SelectedIndex = Logs.IndexOf(findDLG.FileName); 
            }

        }
    }

    public static class ObservableCollectionExtensionsClass
    {
        public static int IndexOf(this ObservableCollection<ProcessInfo> collection, Scan scan)
        {
            for (int i = 0; i < collection.Count; i++)
            {
                if (collection[i].CCScan == scan) return i;
            }
            return -1; 
        }

    }

    public struct ProcessInfo
    {
        public ScanStatus Status { get; set; }
        public Scan CCScan { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string Table { get; set; }
        public string Column { get; set; }
        public int CCHits { get; set; }
        public int CCMasked { get; set; }
        public string Message { get; set; }

        public ProcessInfo(Scan CCScan) : this() 
        {
            this.CCScan = CCScan;
        }
    }
}
