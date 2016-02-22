using log4net;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Management;

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
            timer.Interval = 5 * 1000;  //TODO this should be a configkey
            timer.Elapsed += OnTimedEvent;

            timer.Start();
        }

        private static void OnTimedEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            _logger.Debug("Event handler started");
            _logger.Debug("outtaControls: " + outtaControls.Count);
            try
            {
                string wmiQuery = "select * from Win32_Process where Name='BoomTown.ImportMLS.exe' or Name='BoomTown.DataImport.Photos.exe'"; // procname should be a configkey
                using (var searcher = new ManagementObjectSearcher(wmiQuery))
                using (var retObjectCollection = searcher.Get())
                {
                    var currentlyRunning = new Dictionary<string, DateTime>();
                    foreach (ManagementObject retObject in retObjectCollection)
                    {
                        var uglyStartTime = retObject.GetPropertyValue("CreationDate");
                        var startTime = ManagementDateTimeConverter.ToDateTime(uglyStartTime.ToString());
                        var commandLine = retObject.GetPropertyValue("CommandLine").ToString();
                        var processName = retObject.GetPropertyValue("Name").ToString();
                        string processOwner = "";

                        var arrayOfCommands = commandLine.Split(' ');
                        var boardArg = arrayOfCommands[1].Trim(new char[3] { '-', ':', 'b' });

                        string[] argList = new string[] { string.Empty, string.Empty };
                        int returnVal = Convert.ToInt32(retObject.InvokeMethod("GetOwner", argList));
                        if (returnVal == 0)
                        {
                            processOwner = argList[1] + "\\" + argList[0];
                        }
                        else
                            _logger.WarnFormat("Failed to fetch process owner for Process {0} {1}", processName, commandLine);

                        string key = String.Concat(processName, boardArg);
                        string dummyresult = String.Concat(processName, boardArg);

                        currentlyRunning.Add(key, startTime);

                        DateTime currentStartTime = new DateTime();
                        bool alreadyRunning = outtaControls.TryGetValue(key, out currentStartTime);

                        if (startTime.AddMinutes(1) < DateTime.Now
                            && !string.IsNullOrEmpty(processOwner)
                            && processOwner.StartsWith("NT AUTHORITY", StringComparison.InvariantCultureIgnoreCase))  //TODO why is SYSTEM getting past this?
                        {
                            if (!alreadyRunning)
                            {
                                _logger.ErrorFormat("Process {0} Board ID {1} is OUTTA CONTROL!!!", processName, boardArg);
                                _logger.DebugFormat("Adding key {0} to bag", key);
                                outtaControls.TryAdd(key, startTime);
                            }
                            else if (currentStartTime != startTime)
                                _logger.ErrorFormat("Holy shit Batman, process {0} Board {1} has MULTIPLE FUCKING INSTANCES RUNNING!!!!", processName, boardArg);
                            else
                                _logger.WarnFormat("Process {0} Board ID {1} is STILL OUTTA CONTROL!", processName, boardArg);
                        }
                        else
                        {
                            _logger.DebugFormat("Process {0} Board ID {1} looks good", processName, boardArg);
                            if (alreadyRunning)
                                outtaControls.TryRemove(key, out currentStartTime);
                        }
                        retObject.Dispose();
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
            }
            catch (Exception ex)
            {
                _logger.Warn(String.Concat("Import monitor failed with error: ", ex.ToString()));
            }

            _logger.Debug("Event handler completed");
        }
    }
}
