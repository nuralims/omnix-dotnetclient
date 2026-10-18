using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using XiboClient.Logic;

namespace XiboClient
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        [STAThread]
        protected override void OnStartup(StartupEventArgs e)
        {
            NativeMethods.SetErrorMode(NativeMethods.SetErrorMode(0) |
                           ErrorModes.SEM_NOGPFAULTERRORBOX |
                           ErrorModes.SEM_FAILCRITICALERRORS |
                           ErrorModes.SEM_NOOPENFILEERRORBOX);


            // Ensure our process has the highest priority
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;

#if !DEBUG
            // Catch unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
#endif

            // Load settings
            ApplicationSettings.Default.Load();

            // Limit WPF framerate if configured
            if (ApplicationSettings.Default.LimitFramerate30Fps)
            {
                try
                {
                    System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                        typeof(System.Windows.Media.Animation.Timeline),
                        new FrameworkPropertyMetadata(30));
                    Trace.WriteLine(new LogMessage("App", "LimitFramerate30Fps active: WPF DesiredFrameRate set to 30."), LogType.Info.ToString());
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(new LogMessage("App", "Failed to set DesiredFrameRate. e = " + ex.Message), LogType.Info.ToString());
                }
            }

            // Add the Xibo Tracelistener
            Trace.Listeners.Add(new XiboTraceListener());

            bool shouldQuit = false;
            try
            {
                bool runClient = false;
                bool screenSaver = false;

                // Check for any passed arguments
                if (e.Args.Length > 0)
                {
                    string firstArg = e.Args[0].ToString().ToLower().Trim();
                    if (firstArg == "/p")
                    {
                        // Preview the screen saver - do nothing
                        return;
                    }
                    else if (firstArg == "/s")
                    {
                        // Run screensaver
                        runClient = true;
                        screenSaver = true;
                    }
                    else if (firstArg == "/player" || firstArg == "-player" || firstArg == "--player" || firstArg == "player")
                    {
                        // Direct player launch without showing options first
                        runClient = true;
                    }
                    else
                    {
                        // Any other arg or "o" / "/c": show Settings first
                        runClient = RunSettings();
                    }
                }
                else
                {
                    // By default when opening the executable directly: ALWAYS open Settings first
                    runClient = RunSettings();
                }

                if (runClient)
                {
                    RunClient(screenSaver);
                }
            }
            catch (Exception ex)
            {
                HandleUnhandledException(ex, "Startup", shouldQuit);
            }
            finally
            {
                ShutdownCef();
            }

            // Always flush at the end
            Trace.WriteLine(new LogMessage("Main", "Application Finished"), LogType.Info.ToString());
            Trace.Flush();

            Environment.Exit(0); // end application here as all windows are shown as dialog (sync).
        }

        /// <summary>
        /// Drain CefSharp/Chromium subprocesses before forcing the process to exit.
        /// Without this, Environment.Exit(0) tears the process down while the child
        /// subprocesses still own their IPC/RPC channels and KERNELBASE raises
        /// 0xc0020001 (RPC_S_CALL_FAILED) on the way out.
        /// </summary>
        private static void ShutdownCef()
        {
            try
            {
                if (CefSharp.Cef.IsInitialized == true)
                {
                    CefSharp.Cef.Shutdown();
                }
            }
            catch (Exception cefEx)
            {
                Trace.WriteLine(new LogMessage("Main", "Cef.Shutdown failed: " + cefEx.Message), LogType.Error.ToString());
            }
        }

        /// <summary>
        /// Run the Settings Window
        /// </summary>
        /// <returns>True if the player should be launched immediately</returns>
        private static bool RunSettings()
        {
            // If we are showing the options form, enable visual styles
            OptionsForm windowMain = new OptionsForm();
            windowMain.ShowDialog();
            return windowMain.LaunchPlayerRequested;
        }

        /// <summary>
        /// Run the Player
        /// </summary>
        /// <param name="screenSaver"></param>
        private static void RunClient(bool screenSaver)
        {
            Trace.WriteLine(new LogMessage("Main", "Client Started"), LogType.Info.ToString());

#if !DEBUG
            KeyInterceptor.SetHook();
            MouseInterceptor.SetHook();
#endif

            using (var windowMain = new MainWindow(screenSaver))
            {
                windowMain.ShowDialog();
            }
#if !DEBUG
            KeyInterceptor.UnsetHook();
            MouseInterceptor.UnsetHook();
#endif
        }

        #region Exception Handlers
        static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            HandleUnhandledException(e.Exception, "ThreadException", true);
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            HandleUnhandledException(e.ExceptionObject, "UnhandledException", true);
        }

        static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            HandleUnhandledException(e.Exception, "UnobservedTaskException", false);
        }

        /// <summary>
        /// Event for unhandled exceptions
        /// </summary>
        /// <param name="o"></param>
        static void HandleUnhandledException(Object o, string source, bool quit)
        {
            Exception e = o as Exception;

            // What happens if we cannot start?
            Trace.WriteLine(new LogMessage("Main", "Unhandled Exception: " + source + ": " + e.Message), LogType.Error.ToString());
            Trace.WriteLine(new LogMessage("Main", "Stack Trace: " + e.StackTrace), LogType.Error.ToString());

            // Should we quit or continue
            if (quit)
            {
                try
                {
                    string productName = ApplicationSettings.GetProductNameFromAssembly();

                    // Also write to the event log
                    try
                    {
                        if (!EventLog.SourceExists(productName))
                        {
                            EventLog.CreateEventSource(productName, "Xibo");
                        }

                        EventLog.WriteEntry(productName, e.ToString(), EventLogEntryType.Error);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(new LogMessage("Main", "Couldn't write to event log: " + ex.Message), LogType.Info.ToString());
                    }

                    Trace.Flush();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(new LogMessage("Main", "Unable to write Trace Listeners " + ex.Message), LogType.Info.ToString());

                    // Complete failure, show something to the user in these circumstances.
                    if (quit)
                    {
                        MessageBox.Show("Unhandled Exception: " + ex.Message + ". Stack Trace: " + e.StackTrace, "Fatal Error");
                    }
                }

                // Drain CEF before we hard-exit so Chromium subprocesses don't
                // outlive us and trigger KERNELBASE 0xc0020001 in cleanup.
                ShutdownCef();

                // Exit the application and allow it to be restarted by the Watchdog.
                Environment.Exit(0);
            }
        }

        internal static class NativeMethods
        {
            [DllImport("kernel32.dll")]
            internal static extern ErrorModes SetErrorMode(ErrorModes mode);
        }

        [Flags]
        internal enum ErrorModes : uint
        {
            SYSTEM_DEFAULT = 0x0,
            SEM_FAILCRITICALERRORS = 0x0001,
            SEM_NOALIGNMENTFAULTEXCEPT = 0x0004,
            SEM_NOGPFAULTERRORBOX = 0x0002,
            SEM_NOOPENFILEERRORBOX = 0x8000
        }

        #endregion
    }
}
