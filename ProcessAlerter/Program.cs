using log4net;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Management;
using System.Configuration;

namespace ProcessAlerter
{
    class Program
    {
        //TODO test run on a server
        //TODO set up TopShelf so it runs as a service

        static void Main(string[] args)
        {
            log4net.Config.XmlConfigurator.Configure();

            var instance = new Monitor();
            instance.Go();

            Console.ReadLine(); //TODO take this out once this is a service, just here for debugging
        }
    }
    internal class Monitor
    {
        private static ILog _logger = LogManager.GetLogger("ImportMonitor");
        private static ConcurrentDictionary<string, DateTime> outtaControls = new ConcurrentDictionary<string, DateTime>();

        public void Go()
        {
            var timer = new System.Timers.Timer();

            timer.AutoReset = true;
            var timerInterval = ConfigurationManager.AppSettings["TimerInterval"];
            if (timerInterval == null)
            {
                timer.Interval = 5000;
            }
            else
            {
                timer.Interval = Convert.ToInt32(timerInterval);
            }
            timer.Elapsed += OnTimedEvent;

            timer.Start();
        }

        private static void OnTimedEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            _logger.Debug("Event handler started");
            _logger.Debug("outtaControls: " + outtaControls.Count);
            try
            {
                
                var currentlyRunning = new Dictionary<string, DateTime>();
                foreach (var process in GetProcesses())
                {
                    var arrayOfCommands = process.CommandLine.Split(' ');
                    var boardArg = arrayOfCommands[1].Trim(new char[3] { '-', ':', 'b' });

                    string key = String.Concat(process.Name, boardArg);
                    string dummyresult = String.Concat(process.Name, boardArg);

                    currentlyRunning.Add(key, process.StartTime);

                    DateTime currentStartTime = new DateTime();
                    bool alreadyRunning = outtaControls.TryGetValue(key, out currentStartTime);

                    if (process.StartTime.AddMinutes(1) < DateTime.Now
                        && !string.IsNullOrEmpty(process.Owner)
                        && processOwner.StartsWith("NT AUTHORITY", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (!alreadyRunning)
                        {
                            _logger.ErrorFormat("Process {0} Board ID {1} is OUTTA CONTROL!!!", process.Name, boardArg);
                            _logger.DebugFormat("Adding key {0} to bag", key);
                            outtaControls.TryAdd(key, process.StartTime);
                        }
                        else if (currentStartTime != process.StartTime)
                            _logger.ErrorFormat("Holy shit Batman, process {0} Board {1} has MULTIPLE FUCKING INSTANCES RUNNING!!!!", process.Name, boardArg);
                        else
                            _logger.WarnFormat("Process {0} Board ID {1} is STILL OUTTA CONTROL!", process.Name, boardArg);
                    }
                    else
                    {
                        _logger.DebugFormat("Process {0} Board ID {1} looks good", process.Name, boardArg);
                        if (alreadyRunning)
                            outtaControls.TryRemove(key, out currentStartTime);
                    }
                }
                //zap any outtacontrols that aren't running
                    foreach(var keyValuePair in outtaControls)
                {
                    DateTime dummy = new DateTime();
                    if (!currentlyRunning.ContainsKey(keyValuePair.Key))
                    {
                        outtaControls.TryRemove(keyValuePair.Key, out dummy);
                        _logger.WarnFormat("Process {0} managed to get it's shit under control", keyValuePair.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(String.Concat("Import monitor failed with error: ", ex.ToString()));
            }

            _logger.Debug("Event handler completed");
        }

        private static List<Process> GetProcesses()
        {
            List<Process> retVal = new List<Process>();
			string wmiQuery = "select * from Win32_Process where Name='BoomTown.ImportMLS.exe' or Name='BoomTown.DataImport.Photos.exe'"; // procname should be a configkey
            var procName = ConfigurationManager.AppSettings["ProcName"].Split(';');
            foreach (var proc in procName)
            {
                wmiQuery += "Name= '" + proc + "' or";
            }
            wmiQuery.TrimEnd(new char[3] { ' ', 'o', 'r' });
			
            using (var searcher = new ManagementObjectSearcher(wmiQuery))
            using (var wmiObjects = searcher.Get())
            {
                foreach (ManagementObject wmiObject in wmiObjects)
                {
                    var process = new Process();
                    var uglyStartTime = wmiObject.GetPropertyValue("CreationDate");
                    process.StartTime = ManagementDateTimeConverter.ToDateTime(uglyStartTime.ToString());
                    process.CommandLine = wmiObject.GetPropertyValue("CommandLine").ToString();
                    process.Name = wmiObject.GetPropertyValue("Name").ToString();

                    string[] argList = new string[] { string.Empty, string.Empty };
                    int returnVal = Convert.ToInt32(wmiObject.InvokeMethod("GetOwner", argList));
                    if (returnVal == 0)
                    {
                        process.Owner = argList[1] + "\\" + argList[0];
                    }
                    else
                    {
                        process.Owner = "";
                        _logger.WarnFormat("Failed to fetch process owner for Process {0} {1}", processName, commandLine);
                    }
                    retVal.Add(process);
                    wmiObject.Dispose();
                }
            }
            return retVal;
        }
    }
}
