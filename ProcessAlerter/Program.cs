using log4net;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Management;
using System.Configuration;
using Topshelf;

namespace ProcessAlerter
{
    class Program
    {
        static void Main(string[] args)
        {
            log4net.Config.XmlConfigurator.Configure();

            HostFactory.Run(x =>
            {
                x.Service<Monitor>(s =>
                {
                    s.ConstructUsing(name => new Monitor());
                    s.WhenStarted(m => m.Start());
                    s.WhenStopped(m => m.Stop());
                });
                x.SetServiceName("ProcessAlerter");
                x.SetDescription("Monitors running processes and alerts via log4net");
                x.SetDisplayName("Process Alerter");
                x.RunAsLocalSystem();
            });
        }
    }
    internal class Monitor
    {
        private static ILog _logger = LogManager.GetLogger("ProcessAlerter");
        private static ConcurrentDictionary<string, DateTime> outtaControls = new ConcurrentDictionary<string, DateTime>();
        private static System.Timers.Timer timer = new System.Timers.Timer();
        private static string wmiQuery;
        private static int _timerInterval = 5000;
        public void Start()
        {
            wmiQuery = "select * from Win32_Process where ";
            var procName = ConfigurationManager.AppSettings["ProcName"].Split(';');

            wmiQuery += "Name=" + String.Join(" or Name=", procName.Select(x => "'" + x + "'"));

            timer.AutoReset = false;

            var timerInterval = ConfigurationManager.AppSettings["TimerInterval"];
            if (!String.IsNullOrEmpty(timerInterval))
                _timerInterval = Convert.ToInt32(timerInterval);

            timer.Interval = _timerInterval;
            timer.Elapsed += OnTimedEvent;

            timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            timer.Dispose();
        }

        private static void OnTimedEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            _logger.Debug("Event handler started");
            var tokenSource = new System.Threading.CancellationTokenSource();
            try
            {
                tokenSource.CancelAfter(TimeSpan.FromMilliseconds(_timerInterval));

                new System.Threading.Tasks.Task(
                    () =>
                    {
                        CheckProcesses();
                        tokenSource.Token.ThrowIfCancellationRequested();
                        return;
                    },
                    tokenSource.Token).Wait();
            }
            catch (AggregateException)
            {
                _logger.Error("Timespan exceeded checking the health of running processes. Verify WMI is A-OK and things aren't otherwise stacked up on this server.");
            }
            finally
            {
                tokenSource.Dispose();
            }
            timer.Start();
            _logger.Debug("Event handler completed");
        }

        private static void CheckProcesses()
        {
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
                        && process.Owner.StartsWith("NT AUTHORITY", StringComparison.InvariantCultureIgnoreCase))
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
        }

        private static List<Process> GetProcesses()
        {
            List<Process> retVal = new List<Process>();
            
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
                        _logger.WarnFormat("Failed to fetch process owner for Process {0} {1}", process.Name, process.CommandLine);
                    }
                    retVal.Add(process);
                    wmiObject.Dispose();
                }
            }
            return retVal;
        }
    }
}
