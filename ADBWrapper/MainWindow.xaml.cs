using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using System.IO;
using System.Threading;
using System.Windows.Media.Animation;

namespace ADBWrapper
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private string mAdbScreenshotPath = "";
        private string mAdbPath = "adb.exe";
        private int mScreenRotation = 0;
        private int mScreensizePreWidth = -1;
        private int mScreensizePreHeight = -1;

        private Thread mAdbSendCMDThread;
        private AutoResetEvent mAdbSendCMDEvent = new AutoResetEvent(false);

        private Mutex mAdbCMDMutex = new Mutex();

        enum RefreshMode
        {
            AUTO,
            MUTUAL,
            DISABLE
        }

        private RefreshMode mRefreshMode = RefreshMode.MUTUAL;

        public struct ItemCMD
        {
            public string filename;
            public string arguments;
            public Action<object> onCompleted;
        }

        private Mutex mAdbSendCMDQueueMutex = new Mutex();
        private Queue<ItemCMD> mAdbSendCMDQueue = new Queue<ItemCMD>();
        private bool mAdbClosing = false;

        private Point mMousePressPosition;
        private bool mIsRightPressed = false;
        private DateTime mMousePressedTime = DateTime.Now;

        private DateTime mScreenshotUpdatedTime = DateTime.Now;

        public MainWindow()
        {
            InitializeComponent();

            foreach (var arg in Environment.GetCommandLineArgs())
            {
                Console.WriteLine(arg);
            }
            if (Environment.GetCommandLineArgs().Length >= 2)
                mAdbPath = Environment.GetCommandLineArgs()[1];

            ShowScreenshotFromMemory();

            mAdbSendCMDThread = new Thread(() => {
                ItemCMD adb_cmd = new ItemCMD();
                int queue_size = 0;
                while (!mAdbClosing)
                {
                    mAdbSendCMDEvent.WaitOne();
                    Console.WriteLine("mAdbSendCMDThread " + mAdbClosing);

                    if (mAdbClosing) break;

                    do
                    {
                        mAdbSendCMDQueueMutex.WaitOne();
                        queue_size = mAdbSendCMDQueue.Count;
                        if (queue_size != 0)
                            adb_cmd = mAdbSendCMDQueue.Dequeue();
                        mAdbSendCMDQueueMutex.ReleaseMutex();

                        if (mAdbClosing) break;

                        bool res = true;
                        if (queue_size != 0)
                        {
                            if (adb_cmd.arguments.Contains("screencap") == true)
                            {
                                mScreenshotUpdatedTime = DateTime.Now;
                                MemoryStream mem_stream = new MemoryStream();
                                if ((res = RunCMDtoMEM(adb_cmd.filename, "exec-out screencap -p", ref mem_stream)) && mem_stream.Length != 0)
                                    adb_cmd.onCompleted?.Invoke(mem_stream);
                                else
                                {
                                    adb_cmd.onCompleted?.Invoke(mem_stream);
                                    Thread.Sleep(1000);
                                }
                            }
                            else
                            {
                                res = RunCMD(adb_cmd.filename, adb_cmd.arguments);
                                adb_cmd.onCompleted?.Invoke(res);
                            }
                        }

                        if (mRefreshMode == RefreshMode.AUTO && queue_size <= 1)
                        {
                            mScreenshotUpdatedTime = DateTime.Now;
                            ItemCMD adb_cmd_scr = new ItemCMD();
                            adb_cmd_scr.filename = mAdbPath;
                            adb_cmd_scr.arguments = "exec-out screencap -p";
                            adb_cmd_scr.onCompleted = delegate (object o)
                            {
                                if (o.GetType().Name == "MemoryStream")
                                {
                                    MemoryStream mem = o as MemoryStream;
                                    if (mem != null && mem.Length != 0)
                                        UpdateScreenshot(ref mem);
                                }
                            };
                            mAdbSendCMDQueueMutex.WaitOne();
                            mAdbSendCMDQueue.Enqueue(adb_cmd_scr);
                            mAdbSendCMDQueueMutex.ReleaseMutex();
                            queue_size = 2;
                        }
                    } while (queue_size > 1);
                }
            });
            mAdbSendCMDThread.Start();
        }

        private string mAdbShellCmds = "";
        private bool AdbCMDShell(string cmd, bool isKeepCMD = true)
        {
            if (isKeepCMD)
            {
                mAdbShellCmds += (mAdbShellCmds.Length != 0 ? "; " : "") + cmd;
                UpdateMessage(cmd,MessageLevel.INFO,false);
            }
            return AdbCMD("shell " + cmd);
        }
        private bool AdbInputTap(Point pos)
        {
            string tapCmd = "input tap " + (int)pos.X + " " + (int)pos.Y;
            return AdbCMDShell(tapCmd);
        }

        private bool AdbInputSwipe(Point pos0,Point pos1,int duration = 100)
        {
            string swipeCmd = "input swipe " + (int)pos0.X + " " + (int)pos0.Y + " " + (int)pos1.X + " " + (int)pos1.Y + " " + duration;
            return AdbCMDShell(swipeCmd);
        }

        private bool AdbCMD(string arguments)
        {
            Thread thread = new Thread(new ThreadStart(() => { RunCMD(mAdbPath, arguments); }));
            thread.Start();
            return true;
        }

        private bool RunCMD(string filename, string arguments)
        {
            mAdbCMDMutex.WaitOne();
            System.Diagnostics.Process adb_proc = new System.Diagnostics.Process();
            adb_proc.StartInfo.FileName = filename;
            adb_proc.StartInfo.Arguments = arguments;
            adb_proc.StartInfo.UseShellExecute = false;
            adb_proc.StartInfo.RedirectStandardOutput = true;
            adb_proc.StartInfo.RedirectStandardError = true;
            adb_proc.StartInfo.CreateNoWindow = true;

            adb_proc.OutputDataReceived += new System.Diagnostics.DataReceivedEventHandler((o, d) =>
            {
                Console.WriteLine(d.Data);
            });

            adb_proc.ErrorDataReceived += new System.Diagnostics.DataReceivedEventHandler((o, d) =>
            {
                Console.WriteLine("Error: " + d.Data);
                if (d.Data != null)
                {
                    string msg = d.Data.Trim();
                    Console.WriteLine(msg);
                    this.Dispatcher.Invoke(() =>
                    {
                        UpdateMessage(msg, msg.StartsWith("Warning", StringComparison.CurrentCultureIgnoreCase) ? MessageLevel.WARNING : MessageLevel.ERROR);
                    });
                }
            });

            try
            {
                adb_proc.Start();
                adb_proc.BeginOutputReadLine();
                adb_proc.BeginErrorReadLine();
                adb_proc.WaitForExit();

                mAdbCMDMutex.ReleaseMutex();
                return adb_proc.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Console.WriteLine(e.ToString());
                mAdbCMDMutex.ReleaseMutex();
                return false;
            }
        }

        private bool RunCMDtoMEM(string filename, string arguments, ref MemoryStream mem_stream)
        {
            mAdbCMDMutex.WaitOne();
            System.Diagnostics.Process adb_proc = new System.Diagnostics.Process();
            adb_proc.StartInfo.FileName = filename;
            adb_proc.StartInfo.Arguments = arguments;
            adb_proc.StartInfo.UseShellExecute = false;

            adb_proc.StartInfo.StandardOutputEncoding = System.Text.Encoding.GetEncoding("latin1");
            
            adb_proc.StartInfo.RedirectStandardOutput = true;
            adb_proc.StartInfo.RedirectStandardError = true;
            adb_proc.StartInfo.CreateNoWindow = true;

            //adb_proc.OutputDataReceived += new System.Diagnostics.DataReceivedEventHandler((o, d) =>
            //{
            //    Console.WriteLine(d.Data);
            //});

            adb_proc.ErrorDataReceived += new System.Diagnostics.DataReceivedEventHandler((o, d) =>
            {
                if (d.Data != null)
                {
                    string msg = d.Data.Trim();
                    Console.WriteLine(msg);
                    this.Dispatcher.Invoke(() =>
                    {
                        UpdateMessage(msg,MessageLevel.ERROR);
                    });
                }

            });

            try
            {
                adb_proc.Start();
                //adb_proc.BeginOutputReadLine();
                adb_proc.BeginErrorReadLine();

                int val;
                while ((val = adb_proc.StandardOutput.Read()) != -1)
                    mem_stream.WriteByte((byte)val);

                adb_proc.WaitForExit();

                mAdbCMDMutex.ReleaseMutex();
                return adb_proc.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Console.WriteLine(e.ToString());
                mAdbCMDMutex.ReleaseMutex();
                return false;
            }
        }


        bool UpdateScreenshot(ref MemoryStream mem_stream)
        {
            MemoryStream mem = mem_stream;
            if (mem_stream.Length == 0)
            {
                return false;
            }

            this.Dispatcher.Invoke(() =>
            {
                try
                {
                    RenderOptions.SetBitmapScalingMode(mAdbScreenShot, BitmapScalingMode.HighQuality);

                    BitmapImage src = new BitmapImage();
                    src.BeginInit();
                    src.StreamSource = mem;
                    src.CacheOption = BitmapCacheOption.OnLoad;
                    src.EndInit();

                    mAdbScreenShot.Source = src;

                    TimeSpan diff = DateTime.Now - mScreenshotUpdatedTime;
                    
                    //if (mRefreshMode == RefreshMode.AUTO)
                    mLabelUpdatedInterval.Content = (int)diff.TotalMilliseconds + " ms";
                    mLabelUpdatedIntervalBlur.Content = mLabelUpdatedInterval.Content;


                    DoubleAnimation da = new DoubleAnimation();
                    da.To = 1;
                    da.Duration = new Duration(TimeSpan.FromSeconds(0.5));
                    mAdbScreenShot.BeginAnimation(OpacityProperty, da);

                    if (mScreensizePreWidth != (int)src.PixelWidth || mScreensizePreHeight != (int)src.PixelHeight )
                    {
                        int screen_width, screen_height;
                        if (mScreenRotation == 1 || mScreenRotation == 3)
                        {
                            screen_width = (int)src.PixelHeight;
                            screen_height = (int)src.PixelWidth;
                        }
                        else
                        {
                            screen_width = (int)src.PixelWidth;
                            screen_height = (int)src.PixelHeight;
                        }

                        bool isChangeWindowSize = (screen_width > screen_height && Application.Current.MainWindow.Width < Application.Current.MainWindow.Height) ||
                        (screen_width < screen_height && Application.Current.MainWindow.Width > Application.Current.MainWindow.Height);

                        if (isChangeWindowSize)
                        {
                            double tmp_width = Application.Current.MainWindow.Width;
                            Application.Current.MainWindow.Width = Application.Current.MainWindow.Height;
                            Application.Current.MainWindow.Height = tmp_width;
                        }

                        mScreensizePreWidth = (int)src.PixelWidth;
                        mScreensizePreHeight = (int)src.PixelHeight;
                    }

                    mBtnScreenshot.IsEnabled = true;
                }
                catch (System.NotSupportedException e)
                {
                    Console.WriteLine("Image format not supported " + e.ToString());
                }
            });

            return true;
        }

        bool SaveMemoryImage(ref MemoryStream mem_stream)
        {
            mAdbScreenshotPath = AppDomain.CurrentDomain.BaseDirectory + "Screenshot";
            if (!Directory.Exists(mAdbScreenshotPath))
            {
                Directory.CreateDirectory(mAdbScreenshotPath);
            }

            string screenshot_filename = "screenshot_" + DateTime.Now.ToString("yy_MM_dd_HH_mm_ss") + ".png";
            screenshot_filename = mAdbScreenshotPath + "\\" + screenshot_filename;

            bool res = false;
            using (FileStream file = new FileStream(screenshot_filename, FileMode.Create, System.IO.FileAccess.Write))
            {
                mem_stream.WriteTo(file);
                res = file.Length != 0;
            }
            return res;
        }

        bool ShowScreenshotFromMemory(bool isSaveFile = false)
        {
            this.Dispatcher.Invoke(() =>
            {
                DoubleAnimation da = new DoubleAnimation();
                da.To = 0.5;
                da.Duration = new Duration(TimeSpan.FromSeconds(0.5));
                mAdbScreenShot.BeginAnimation(OpacityProperty, da);
            });

            mScreenshotUpdatedTime = DateTime.Now;

            ItemCMD adb_cmd = new ItemCMD();
            adb_cmd.filename = mAdbPath;
            adb_cmd.arguments = "exec-out screencap -p";
            adb_cmd.onCompleted = delegate (object o)
            {
                if (o.GetType().Name == "MemoryStream")
                {
                    MemoryStream mem = o as MemoryStream;
                    if (mem != null && mem.Length != 0)
                    {
                        if (isSaveFile)
                            SaveMemoryImage(ref mem);
                        UpdateScreenshot(ref mem);
                    }
                    else
                    {
                        this.Dispatcher.Invoke(() =>
                        {
                            DoubleAnimation da = new DoubleAnimation();
                            da.To = 1;
                            da.Duration = new Duration(TimeSpan.FromSeconds(0.5));
                            mAdbScreenShot.BeginAnimation(OpacityProperty, da);
                        });
                    }
                }
            };
            mAdbSendCMDQueueMutex.WaitOne();
            mAdbSendCMDQueue.Enqueue(adb_cmd);
            mAdbSendCMDQueueMutex.ReleaseMutex();

            mAdbSendCMDEvent.Set();

            return true;
        }
        

        private void BtnDebug_Click(object sender, RoutedEventArgs e)
        {

        }


        private void BtnPower_Click(object sender, RoutedEventArgs e)
        {
            AdbCMDShell("input keyevent 26");
            if (mRefreshMode != RefreshMode.AUTO)
                ShowScreenshotFromMemory();
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            AdbCMDShell("input keyevent 3");
            if (mRefreshMode != RefreshMode.AUTO)
                ShowScreenshotFromMemory();
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            AdbCMDShell("input keyevent 4");
            if (mRefreshMode != RefreshMode.AUTO)
                ShowScreenshotFromMemory();
        }

        private void BtnScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (mRefreshMode != RefreshMode.AUTO)
                ShowScreenshotFromMemory(true);
            mBtnScreenshot.IsEnabled = false;
        }

        private void BtnRotLeft_Click(object sender, RoutedEventArgs e)
        {
            mScreenRotation--;
            if (mScreenRotation < 0) mScreenRotation = 3;

            mAdbScreenShot.LayoutTransform = new RotateTransform(90 * mScreenRotation);

            double tmp_width = Application.Current.MainWindow.Width;
            Application.Current.MainWindow.Width = Application.Current.MainWindow.Height;
            Application.Current.MainWindow.Height = tmp_width;

        }

        private void BtnRotRight_Click(object sender, RoutedEventArgs e)
        {
            mScreenRotation++;
            if (mScreenRotation > 3) mScreenRotation = 0;
            mAdbScreenShot.LayoutTransform = new RotateTransform(90 * mScreenRotation);

            double tmp_width = Application.Current.MainWindow.Width;
            Application.Current.MainWindow.Width = Application.Current.MainWindow.Height;
            Application.Current.MainWindow.Height = tmp_width;
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            AdbCMDShell("am start -a android.settings.SETTINGS");
        }

        private void BtnSettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender == mMenuItemSettingsSysInfo)
                AdbCMDShell("am start -n com.android.settings/.Settings\\$SystemDashboardActivity");
            else if (sender == mMenuItemSettingsDev)
                AdbCMDShell("am start -n com.android.settings/.Settings\\$DevelopmentSettingsActivity");
            else if (sender == mMenuItemSettingsDevDashboard)
                AdbCMDShell("am start -n com.android.settings/.Settings\\$DevelopmentSettingsDashboardActivity");
            else if (sender == mMenuItemCamera)
                AdbCMDShell("am start -a android.media.action.IMAGE_CAPTURE");
            else if (sender == mMenuItemCameraMTK)
                AdbCMDShell("am start -n com.mediatek.camera/.CameraLauncher");
            else if (sender == mMenuItemMTKLogger)
                AdbCMDShell("am start -n com.mediatek.mtklogger/.MainActivity");
        }

        private void UpdateBtnAutoRefresh()
        {
            mScreenshotUpdatedTime = DateTime.Now;
            if (mRefreshMode == RefreshMode.DISABLE)
            {
                DoubleAnimation da = new DoubleAnimation();
                da.To = 0.5;
                da.Duration = new Duration(TimeSpan.FromSeconds(0.25));
                mBtnAutoRefresh.BeginAnimation(OpacityProperty, da);

                mImgRefreshAuto.Visibility = Visibility.Collapsed;
                mImgRefreshMutually.Visibility = Visibility.Visible;
            }
            else if (mRefreshMode == RefreshMode.AUTO)
            {
                DoubleAnimation da = new DoubleAnimation();
                da.To = 1;
                da.Duration = new Duration(TimeSpan.FromSeconds(0.25));
                mBtnAutoRefresh.BeginAnimation(OpacityProperty, da);

                mImgRefreshAuto.Visibility = Visibility.Visible;
                mImgRefreshMutually.Visibility = Visibility.Collapsed;
                mAdbSendCMDEvent.Set();
            }
            else if (mRefreshMode == RefreshMode.MUTUAL)
            {
                mImgRefreshAuto.Visibility = Visibility.Collapsed;
                mImgRefreshMutually.Visibility = Visibility.Visible;
            }
        }
        private void BtnAutoRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (mRefreshMode == RefreshMode.MUTUAL)
            {
                mRefreshMode = RefreshMode.DISABLE;
            }
            else if (mRefreshMode == RefreshMode.DISABLE)
            {
                mRefreshMode = RefreshMode.AUTO;
            }
            else if (mRefreshMode == RefreshMode.AUTO)
            {
                mRefreshMode = RefreshMode.MUTUAL;
            }
            UpdateBtnAutoRefresh();
        }

        private void BtnAutoRefreshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender == mMenuItemQSAuto)
                mRefreshMode = RefreshMode.AUTO;
            else if (sender == mMenuItemQSMutual)
                mRefreshMode = RefreshMode.MUTUAL;
            else if (sender == mMenuItemQSDisable)
                mRefreshMode = RefreshMode.DISABLE;
            UpdateBtnAutoRefresh();
        }

        private void AdbScreenShot_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Point pos = e.GetPosition(mAdbScreenShot);
            Point adb_pos = new Point(pos.X * mAdbScreenShot.Source.Width / mAdbScreenShot.ActualWidth,
                pos.Y * mAdbScreenShot.Source.Height / mAdbScreenShot.ActualHeight);

            mMousePressPosition = adb_pos;
            mIsRightPressed = e.RightButton == MouseButtonState.Pressed;

            mMousePressedTime = DateTime.Now;
        }

        private void AdbScreenShot_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Point pos = e.GetPosition(mAdbScreenShot);
            Point adb_pos = new Point(pos.X * mAdbScreenShot.Source.Width / mAdbScreenShot.ActualWidth,
                pos.Y * mAdbScreenShot.Source.Height / mAdbScreenShot.ActualHeight);

            TimeSpan diff = DateTime.Now - mMousePressedTime;

            if (mIsRightPressed)
                ShowScreenshotFromMemory();
            else
            {
                if (Math.Abs(adb_pos.X - mMousePressPosition.X) < 10 &&
                    Math.Abs(adb_pos.Y - mMousePressPosition.Y) < 10)
                    AdbInputTap(mMousePressPosition);
                else
                    AdbInputSwipe(mMousePressPosition, adb_pos, (int)diff.TotalMilliseconds);

                if (mRefreshMode == RefreshMode.MUTUAL)
                    ShowScreenshotFromMemory();
            }
        }

        private void AdbScreenShot_MouseMove(object sender, MouseEventArgs e)
        {
            Point pos = e.GetPosition(mAdbScreenShot);
            Point adb_pos = new Point(pos.X * mAdbScreenShot.Source.Width / mAdbScreenShot.ActualWidth,
                pos.Y * mAdbScreenShot.Source.Height / mAdbScreenShot.ActualHeight);

            this.Title = Application.Current.MainWindow.GetType().Assembly.GetName().Name + String.Format(" - {0:0.},{1:0.} @ {2}x{3}", adb_pos.X, adb_pos.Y, mAdbScreenShot.Source.Width, mAdbScreenShot.Source.Height);
            //Console.WriteLine(Application.Current.MainWindow.GetType().Assembly.GetName().Name + " " + adb_pos.ToString());
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            mAdbClosing = true;
            mRefreshMode = RefreshMode.DISABLE;
            mAdbSendCMDQueueMutex.WaitOne();
            mAdbSendCMDQueue.Clear();
            mAdbSendCMDQueueMutex.ReleaseMutex();

            mAdbCMDMutex.WaitOne();
            mAdbCMDMutex.ReleaseMutex();

            mAdbSendCMDEvent.Set();
            //Console.WriteLine("before Join....");
            //mAdbSendCMDThread.Join();
        }

        private void BtnScrMenuItem_Click(object sender, RoutedEventArgs e)
        {
            String path = AppDomain.CurrentDomain.BaseDirectory;
            mAdbScreenshotPath = AppDomain.CurrentDomain.BaseDirectory + "Screenshot";
            System.Diagnostics.Process.Start("explorer.exe", Directory.Exists(path + "Screenshot")?path + "Screenshot":path);
        }

        private void BtnPowerMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender == mMenuItemReboot)
                AdbCMD("reboot");
            else if (sender == mMenuItemKillServer)
                AdbCMD("kill-server");
            else if (sender == mMenuItemHideMsg)
            {
                DoubleAnimation da = new DoubleAnimation();
                da.To = mRichTextBoxMessage.Opacity < 0.5 ? 1:0;
                da.Duration = new Duration(TimeSpan.FromSeconds(0.25));
                mRichTextBoxMessage.BeginAnimation(OpacityProperty, da);
                mRichTextBoxMessageBlur.BeginAnimation(OpacityProperty, da);

                //if (mRichTextBoxMessage.Visibility == Visibility.Visible)
                //{
                //    mRichTextBoxMessage.Visibility = Visibility.Hidden;
                //    mRichTextBoxMessageBlur.Visibility = Visibility.Hidden;
                //}
                //else
                //{
                //    mRichTextBoxMessage.Visibility = Visibility.Visible;
                //    mRichTextBoxMessageBlur.Visibility = Visibility.Visible;
                //}
            }
            else if (sender == mMenuItemClrMsg)
            {
                mAdbShellCmds = "";
                mRichTextBoxMessage.Document.Blocks.Clear();
                mRichTextBoxMessageBlur.Document.Blocks.Clear();
            }
            else if (sender == mMenuItemCopyTouchCmd)
            {
                string cmd = "adb shell \"" + mAdbShellCmds + "\"";
                Clipboard.SetText(cmd);
                Console.WriteLine(cmd);
            }
        }

        enum MessageLevel { INFO, WARNING, ERROR};
        private void UpdateMessage(string msg, MessageLevel msgLevel = MessageLevel.INFO, bool isSkipRepeat = true)
        {
            msg = msg.Trim();
            if (msg.Length == 0) return;


            string richText = new TextRange(mRichTextBoxMessage.Document.ContentStart, mRichTextBoxMessage.Document.ContentEnd).Text;
            if (!isSkipRepeat || (isSkipRepeat && !richText.StartsWith(msg)))
            {
                TextRange tr = new TextRange(mRichTextBoxMessage.Document.ContentStart, mRichTextBoxMessage.Document.ContentStart);
                tr.Text = msg + "\r";
                tr.ApplyPropertyValue(TextElement.ForegroundProperty,
                    msgLevel == MessageLevel.ERROR ? Brushes.Red :
                    msgLevel == MessageLevel.WARNING ? Brushes.Yellow :
                    msgLevel == MessageLevel.INFO ? Brushes.White : Brushes.White);

                tr = new TextRange(mRichTextBoxMessageBlur.Document.ContentStart, mRichTextBoxMessageBlur.Document.ContentStart);
                tr.Text = msg + "\r";
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Black);
            }
        }

    }
}
