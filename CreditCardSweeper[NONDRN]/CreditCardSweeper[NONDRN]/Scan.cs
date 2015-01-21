using System;
using System.Collections.Generic;
using System.Configuration; 
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail; 
using System.Text;
using System.Text.RegularExpressions;

namespace CreditCardSweeper
{
    public enum EventType { ScanStart, TableScanStart, ColumnScanStart, CCDataFound, CCDataMasked, ScanCompleted, Error };

    public class ScanEventArgs : EventArgs
    {
        public EventType Type; 
        public string Server;
        public string Database;
        public string message;
        public int hits;
        public int masked; 
    }

    public class DataEventArgs: ScanEventArgs
    {
        public string Table;
        public string Column;
    }

    public class Scan
    {
        public EventHandler<ScanEventArgs> ScanEventOccured;
        public EventHandler<DataEventArgs> DataEventOccured;

        public bool IsSavingScanLogs { get; private set; }
        public bool IsMaskingData { get; private set; }
        public string LogsDirectory { get; private set; }
        public string ErrorLog { get { return errorLog; } }
        public string ScanLog { get { return scanLog; } } 

        private SqlConnectionStringBuilder ConnectionString;

        public List<String> CCPatt = new List<string>();
        private string dateTime = DateTime.Now.ToString("MMddyy_hhmmss");
        private string errorLog = String.Empty;
        private string scanLog = String.Empty; 

        public Scan(string server, string database, string saveLogsTo, bool saveScanLogsToSystem = false, bool IsMaskingData = false)
        {
            if (String.IsNullOrEmpty(database))
            {
                throw new ArgumentException("Please provide database name.");
            }

            if (String.IsNullOrEmpty(server))
            {
                throw new ArgumentException("Please provide a server.");
            }

            ConnectionString = new SqlConnectionStringBuilder();
            ConnectionString.DataSource = server;
            ConnectionString.InitialCatalog = database;
            ConnectionString.IntegratedSecurity = true;

            this.IsMaskingData = IsMaskingData;
            this.IsSavingScanLogs = saveScanLogsToSystem;

            string fileFriendlyServerName = server.RemoveInvalidFilePathChars() + System.IO.Path.DirectorySeparatorChar;
            string logDirectory = System.IO.Path.Combine(saveLogsTo, fileFriendlyServerName);

            try
            {
                if (!Directory.Exists(saveLogsTo))
                {
                    Directory.CreateDirectory(saveLogsTo);
                }

                if(!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                LogsDirectory = saveLogsTo;
                errorLog = System.IO.Path.Combine(logDirectory, GenerateFileName(error: true, database: database)); 
            }
            catch (Exception e)
            {
                throw new ArgumentException("Valid Log directory not provided.", e); 
            }

            if (IsSavingScanLogs)
            {
                scanLog = System.IO.Path.Combine(logDirectory, GenerateFileName(error: false, database: database));
            }
            else
            {
                scanLog = Path.GetTempFileName(); 
            }
        }

        public void Sweep()
        {            
            string database = ConnectionString.InitialCatalog;
            string server = ConnectionString.DataSource; 
            Dictionary<String, List<CC_Hit>> ccdata_hits = new Dictionary<string, List<CC_Hit>>();

            using (SqlConnection connection = new SqlConnection(this.ConnectionString.ConnectionString))
            {
                if (!HavePatterns())
                {
                    string errorMessage = "No regular expressions to use for scan.";
                    WriteError(errorMessage); 
                    throw new ArgumentException(errorMessage);
                }
                try
                {
                    connection.Open();
                    ScanStart();
                    ScanComplete(ScanTables(connection, TablesGrab(connection)));
                }
                catch (SqlException e)
                {
                    string errorMessage = Environment.NewLine + "Error Occured: " + e.Message;
                    WriteError(errorMessage);
                    throw; 
                }
            }
        }

        private bool HavePatterns()
        {
            try
            {
                string[] patterns = File.ReadAllLines("CCREGEX.txt");

                foreach (string p in patterns)
                {
                    string patt = p.Trim(); 
                    int commentStart = p.IndexOf('#');
                    if (commentStart >= 0) patt = p.Remove(commentStart).Trim();

                    if (!String.IsNullOrEmpty(patt)) CCPatt.Add(patt); 

                }

                if (CCPatt.Count == 0)
                {
                    string errorMessage = "No REGEX patterns provided in CCREGEX.txt";
                    WriteError(errorMessage);
                    return false; 
                }
                return true; 
            }
            catch (Exception e)
            {
                string errorMessage = "Failed to read regex patterns from CCREGEX.txt due to "+ e.GetType().Name + "; " + e.Message;
                WriteError(errorMessage);
                throw e; 
            }
        }
        
        private static Dictionary<String, List<String>> TablesGrab(SqlConnection connection)
        {
            Dictionary<String, List<String>> tables = new Dictionary<string, List<string>>(); 
            Console.WriteLine("Grabbing table data...");

            using (SqlCommand select = new SqlCommand(connection.DataSource.Contains("DRN-DB-1") ?
                "SELECT TABLE_NAME, COLUMN_NAME " + "FROM INFORMATION_SCHEMA.COLUMNS C " +
                "WHERE (table_name = 'trkchg' OR table_name = 'trkfile')" +
                "AND DATA_TYPE IN ('varchar', 'nchar', 'char')" +
                "AND CHARACTER_MAXIMUM_LENGTH > 12 ORDER BY TABLE_NAME ASC;" 
                : "SELECT TABLE_NAME, COLUMN_NAME " +
                "FROM INFORMATION_SCHEMA.COLUMNS C " +
                "WHERE EXISTS( " +
                "SELECT * FROM sys.partitions " +
                "WHERE object_id=object_id(c.TABLE_NAME) " +
                "AND rows>0) AND DATA_TYPE IN ('varchar', 'nvarchar', 'nchar', 'char') " +
                "AND CHARACTER_MAXIMUM_LENGTH > 12 ORDER BY TABLE_NAME ASC;"
                , connection))
            using (SqlDataReader reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    string table = reader.GetString(0);
                    string column = reader.GetString(1);
                    tables.ExtendedAdd(table, column);
                }

            }

            return tables; 
        }

        private Dictionary<String, List<CC_Hit>> ScanTables(SqlConnection connection, Dictionary<String, List<String>> ttables)
        {
            Dictionary<string, List<CC_Hit>> hits = new Dictionary<string, List<CC_Hit>>();

            using (SqlCommand grab = new SqlCommand())
            {
                grab.Connection = connection;

                foreach (string table in ttables.Keys)
                {
                    TableScanStart(table); 
                    foreach (string column in ttables[table])
                    {
                        ColumnScanStart(table, column); 

                        int count = 0;
                        string hit = String.Empty;
                        int maskcount = 0;
                        PreviousHit lastHit = new PreviousHit(String.Empty, 0, false);

                        grab.CommandText = "SELECT [" + column + "] FROM [" + table + "] WHERE DATALENGTH([" + column + "]) >= 12 AND [" + column + "] IS NOT NULL";
                        using (SqlDataReader gread = grab.ExecuteReader())
                        {
                            while (gread.Read())
                            {
                                if (gread.IsDBNull(0)) continue;
                                else
                                {
                                    string value = gread.GetString(0);
                                    if (value == (lastHit.value ?? String.Empty))
                                    {
                                        if (lastHit.hitCount > 0 && !IsMaskingData)
                                        {
                                            count+= lastHit.hitCount;
                                            continue;
                                        }
                                        else if (lastHit.hitCount > 0 && IsMaskingData)
                                        {
                                            if (lastHit.wasMasked)
                                            {
                                                maskcount+= lastHit.hitCount;
                                                continue;
                                            }
                                        }
                                    }
                                    string strippedValue = value.Replace(" ", String.Empty).Replace("-", String.Empty);
                                    IEnumerable<Match> matches = FindCreditCardData(strippedValue);
                                    if (matches != null && matches.Count() > 0)
                                    {
                                        ;
                                        int hitcount = 0;
                                        foreach (Match match in matches)
                                        {
                                            if (match.Success)
                                            {
                                                hitcount += match.Captures.Count;
                                                hit = GenerateMask(match.Value, match.Value);
                                                break;
                                            }
                                        }
                                        lastHit.hitCount = hitcount; 
                                        count += hitcount; 
                                        CCDataFound(table, column, hitcount); 

                                        if (IsMaskingData)
                                        {
                                            int maskResult = MaskData(connection, matches, table, column, value, strippedValue);
                                            if (maskResult > -1)
                                            {
                                                maskcount += maskResult;
                                                CCDataMasked(table, column, hitcount, maskResult); 
                                            }
                                            lastHit.wasMasked = (maskResult > 0);
                                        }
                                    }
                                    else lastHit.hitCount = 0;

                                    lastHit.value = value;
                                }
                            }

                            grab.Cancel();
                        }

                        if (count > 0)
                        {
                            hits.ExtendedAdd(table, (IsMaskingData) ? new CC_Hit(column, count, hit, maskcount) : new CC_Hit (column, count, hit));
                            if (IsMaskingData)
                            {
                                if (count == maskcount)
                                {
                                    WriteLog(scanLog, "All credit card data found was successfully masked.");
                                }
                                else if (maskcount == 0)
                                {
                                    WriteLog(scanLog, "Found credit card data but failed to mask it.");
                                }
                                else if (count > maskcount)
                                {
                                    WriteLog(scanLog, "Unable to mask all data.");
                                }
                            }
                            else WriteLog(scanLog, "Found credit card data.");
                        }
                    }
                }
            }

            return hits; 
        }

        private IEnumerable<Match> FindCreditCardData(string input)
        {
            IEnumerable<Match> allMatches = null;
            if (Regex.IsMatch(input, @"\d"))
            {
                foreach (string pattern in CCPatt)
                {
                    MatchCollection matches = Regex.Matches(input, pattern);
                    if (matches.Count > 0)
                    {
                        if (matches.Count == 1 && matches[0].Value == input)
                        {
                            return matches.OfType<Match>();
                        }

                        if (allMatches == null) allMatches = matches.OfType<Match>();
                        else
                        {
                            allMatches = allMatches.Union(matches.OfType<Match>()).Where(m => m.Success);
                        }
                    }
                }
            }
            return allMatches;
        }

        internal string GenerateFileName(bool error, string database = null)
        {
            string prefix = ((error) ? "CCError_" : "CCScan_") + dateTime + ".log";
            if (String.IsNullOrEmpty(database))
            {
                if (ConnectionString == null) return prefix;
                else database = ConnectionString.InitialCatalog; 
            }
            string fileFriendlyDatabaseName = database.RemoveInvalidFilePathChars();
            return fileFriendlyDatabaseName + "_" + prefix; 
        }

        #region MaskMethods

        private static string GenerateMask(string data, string hit)
        {
            string maskedData = hit.Substring(0, 4);
            for (int i = 4; i < hit.Length - 3; i++)
            {
                maskedData += '*';
            }
            maskedData += hit.Substring(maskedData.Length, 3);
            return data.Replace(hit, maskedData);
        }

        private bool ReplaceWithMask(string connectionStr, string table, string column, string value, string cleanString)
        {

            try
            {
                using (SqlConnection mconnection = new SqlConnection(connectionStr))
                {
                    mconnection.Open();
                    using (SqlCommand mask = new SqlCommand("UPDATE [" + table + "] SET [" + column + "] = @maskedValue WHERE [" + column + "] = replace(replace(@oldValue,'\r',Char(13)),'\n',Char(10));"))
                    {
                        mask.Connection = mconnection;
                        mask.Parameters.AddWithValue("@maskedValue", cleanString);
                        mask.Parameters.AddWithValue("@oldValue", value);
                        WriteLog(scanLog, "Attempting to mask data using the following command: " + mask.CommandText);
                        return (mask.ExecuteNonQuery() > 0);
                    }
                }
            }
            catch (Exception e)
            {
                WriteError("The following error occured while masking: " + e.GetType().Name + ": " + e.Message);
                return false;
            }

        }

        private int MaskData(SqlConnection connection, IEnumerable<Match> hits, string table, string column, string value, string strippedValue)
        {
            int maskedCount = 0;
            try
            {
                string cleanString = value;
                foreach (Match match in hits)
                {
                    if (match.Success)
                    {
                        cleanString = GenerateMask(strippedValue, match.Value);
                        strippedValue = cleanString;
                        maskedCount += match.Captures.Count; 
                    }
                }

                if (cleanString != value)
                {
                    cleanString = cleanString.UndoReplace(" ", value.Replace("-", String.Empty).AllIndexesOf(' '));
                    cleanString = cleanString.UndoReplace("-", value.AllIndexesOf('-'));
                    return (ReplaceWithMask(connection.ConnectionString, table, column, value, cleanString)) ? maskedCount : 0;
                }
            }
            catch (Exception e)
            {
                WriteError("The following error occured while masking: " + e.GetType().Name + ": " + e.Message);
                return 0;
            }
            return -1;
        }

        #endregion MaskMethods

        private void WriteLog(string filename, string contents)
        {
            string path = System.IO.Path.Combine(LogsDirectory, filename);
            if (File.Exists(path))
            {
                File.AppendAllText(path, Environment.NewLine + contents);
            }
            else
            {
                File.WriteAllText(path, contents);
            }
        }

        private void WriteError(string message)
        {
            string time = DateTime.Now.ToString("hh:mm:ss");
            string line = "[" + time + "] " + message;
            WriteLog(errorLog, line);
            WriteLog(scanLog, "Scan failed due to error at " + time + ". See " + errorLog + " for details.");
        }

        #region EventMethods

        private void ScanStart()
        {
            ScanEventArgs startArgs = new ScanEventArgs();

            startArgs.Server = ConnectionString.DataSource;
            startArgs.Database = ConnectionString.InitialCatalog;
            startArgs.hits = startArgs.masked = 0;
            startArgs.Type = EventType.ScanStart;
            startArgs.message = "Beginning scan of " + startArgs.Database + " on " + startArgs.Server;

            WriteLog(scanLog, startArgs.message);
            OnScanEventOccured(startArgs); 
        }

        private void TableScanStart(string table)
        {
            DataEventArgs tscanArgs = new DataEventArgs();
            tscanArgs.Type = EventType.TableScanStart;
            tscanArgs.Server = ConnectionString.DataSource;
            tscanArgs.Database = ConnectionString.InitialCatalog;
            tscanArgs.Table = table;
            tscanArgs.message = "Searching the " + table + " table...";

            WriteLog(scanLog, tscanArgs.message);
            OnDataEventOccured(tscanArgs); 
        }

        private void ColumnScanStart(string table, string column)
        {
            DataEventArgs cscanArgs = new DataEventArgs();
            cscanArgs.Type = EventType.ColumnScanStart; 
            cscanArgs.Server = ConnectionString.DataSource;
            cscanArgs.Database = ConnectionString.InitialCatalog;
            cscanArgs.Table = table;
            cscanArgs.Column = column;
            cscanArgs.message = "Scanning " + column + "...";

            WriteLog(scanLog, cscanArgs.message);
            OnDataEventOccured(cscanArgs); 

        }

        private void CCDataFound(string table, string column, int hits)
        {
            DataEventArgs ccHitArgs = new DataEventArgs();
            ccHitArgs.Type = EventType.CCDataFound;
            ccHitArgs.Server = ConnectionString.DataSource;
            ccHitArgs.Database = ConnectionString.InitialCatalog;
            ccHitArgs.Table = table;
            ccHitArgs.Column = column;
            ccHitArgs.hits = hits;
            ccHitArgs.message = "Found credit card data.";

            WriteLog(scanLog, ccHitArgs.message);
            OnDataEventOccured(ccHitArgs); 
        }

        private void CCDataMasked(string table, string column, int hits, int maskcount)
        {
            DataEventArgs ccMaskedArgs = new DataEventArgs();
            ccMaskedArgs.Type = EventType.CCDataMasked;
            ccMaskedArgs.Server = ConnectionString.DataSource;
            ccMaskedArgs.Database = ConnectionString.InitialCatalog;
            ccMaskedArgs.Table = table;
            ccMaskedArgs.Column = column;
            ccMaskedArgs.hits = hits;
            ccMaskedArgs.masked = maskcount;

            if (maskcount > 0)
            {
                WriteLog(scanLog, "Credit card data successfully masked.");
            }
            else
            {
                WriteLog(scanLog, "Failed to mask credit card data.");
            }

            WriteLog(scanLog, ccMaskedArgs.message);
            OnDataEventOccured(ccMaskedArgs);
        }

        private void ScanComplete(Dictionary<String, List<CC_Hit>> ccHits)
        {
            ScanEventArgs finishArgs = new ScanEventArgs();

            finishArgs.Server = ConnectionString.DataSource;
            finishArgs.Database = ConnectionString.InitialCatalog;
            finishArgs.hits = finishArgs.masked = 0;
            finishArgs.Type = EventType.ScanCompleted;

            foreach (List<CC_Hit> hits in ccHits.Values)
            {
                foreach (CC_Hit hit in hits)
                {
                    finishArgs.hits += hit.NumberOfHits;
                    finishArgs.masked += hit.MaskedData;
                }
            }

            finishArgs.message = "Found " + finishArgs.hits + " pieces of cc data in " + finishArgs.Database + " database on " + finishArgs.Server;


            if (ccHits.Count > 0)
            {
                WriteLog(scanLog, finishArgs.message + " in the following locations:");
                WriteLog(scanLog, ccHits.Print(PrintMasks: IsMaskingData));
            }
            else
            {
                WriteLog(scanLog, finishArgs.message + ".");
            }

            if (IsMaskingData) finishArgs.message += " Successfully masked " + finishArgs.masked + " out of " + finishArgs.hits + ".";

            OnScanEventOccured(finishArgs); 
        }

        protected virtual void OnScanEventOccured(ScanEventArgs e)
        {
            EventHandler<ScanEventArgs> handler = ScanEventOccured;
            if (handler != null) handler(this, e); 
        }

        protected virtual void OnDataEventOccured(DataEventArgs e)
        {
            EventHandler<DataEventArgs> handler = DataEventOccured;
            if (handler != null) handler(this, e); 
        }

        #endregion

    }

    public static class DictionaryExtensionsClass
    {
        public static void ExtendedAdd(this Dictionary<String, List<String>> dict, string key, string value)
        {
            if (dict.ContainsKey(key))
            {
                dict[key].Add(value);
            }
            else
            {
                dict.Add(key, new List<string>(new string[] { value }));
            }
        }

        public static void ExtendedAdd(this Dictionary<String, List<CC_Hit>> dict, string key, CC_Hit value)
        {
            if (dict.ContainsKey(key))
            {
                dict[key].Add(value);
            }
            else
            {
                dict.Add(key, new List<CC_Hit>(new CC_Hit[] { value }));
            }
        }

        public static string Print(this Dictionary<String, List<String>> dict, bool DoIndent = true)
        {
            string output = String.Empty; 

            foreach (string key in dict.Keys)
            {
                if (DoIndent) output += ("\t"); 
                output += key + Environment.NewLine;
                foreach (string value in dict[key])
                {
                    if (DoIndent) output += "\t";
                    output += "\t" + key + Environment.NewLine;
                }
            }

            return output; 
        }

        public static string Print(this Dictionary<String, List<CC_Hit>> dict, bool DoIndent = true, bool PrintMasks = false)
        {
            string output = String.Empty;

            foreach (string key in dict.Keys)
            {
                if (DoIndent) output += ("\t");
                output += key + Environment.NewLine;
                foreach (CC_Hit value in dict[key])
                {
                    if (DoIndent) output += "\t";
                    output += "\t" + value.ColumnName + ": ";

                    if (PrintMasks)
                    {
                        output += "Masked " + value.MaskedData + " out of " + value.NumberOfHits + " hits (s)";
                    }
                    else
                    {
                        output += value.NumberOfHits + " hit(s)";
                    }

                    output += " i.e. " + value.SampleHit + Environment.NewLine; 
                }
            }

            return output;
        }
    }

    public static class StringExtensionsClass
    {
        //based on code found at http://stackoverflow.com/questions/12765819/more-efficient-way-to-get-all-indexes-of-a-character-in-a-string
        public static IEnumerable<int> AllIndexesOf(this string str, char c)
        {
            int minIndex = str.IndexOf(c);
            while (minIndex != -1)
            {
                yield return minIndex;
                if (minIndex >= str.Length) break;
                minIndex = str.IndexOf(c, minIndex + 1);
            }
        }

        public static string UndoReplace(this string str, string toBeInserted, IEnumerable<int> locations)
        {
            if (locations == null || locations.Count() == 0) return str;
            else
            {
                string newStr = String.Empty;
                for (int i = 0, j = 0; i < str.Length + locations.Count(); i++)
                {
                    if (locations.Contains<int>(i))
                    {
                        newStr += toBeInserted;
                    }
                    else
                    {
                        if (j < str.Length)
                        {
                            newStr += str[j++];
                        }
                    }
                }
                return newStr;
            }
        }

        public static string RemoveInvalidFilePathChars(this string str)
        {
            string clean = str;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                clean = clean.Replace(c.ToString(), String.Empty);
            }
            foreach (char c in System.IO.Path.GetInvalidPathChars())
            {
                clean = clean.Replace(c.ToString(), String.Empty);
            }
            return clean;
        }
    }

    public struct CC_Hit
    {
        public string ColumnName { get; set; }
        public int NumberOfHits { get; set; }
        public string SampleHit { get; set; }
        public int MaskedData { get; set; } 

        public CC_Hit(string column, int hits, string sample): this() 
        {
            this.ColumnName = column;
            this.NumberOfHits = hits;
            this.SampleHit = sample; 
        }

        public CC_Hit(string column, int hits, string sample, int maskedHits)
            : this(column, hits, sample)
        {
            this.MaskedData = maskedHits;
        }
    }

    public struct PreviousHit
    {
        public string value;
        public int hitCount; 
        public bool wasMasked;

        public PreviousHit(string value, int hitCount, bool wasMasked)
            : this()
        {
            this.value = value;
            this.hitCount = hitCount;
            this.wasMasked = wasMasked;
        }
    }
}
