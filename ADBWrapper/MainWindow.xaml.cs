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
using System.Xml;
using System.Text.RegularExpressions;

namespace ADBWrapper
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        private string mAdbScreenshotPath = "";
        private string mAdbPath = "adb.exe";
        private int mScreenRotation = 0;
        private int mScreensizePreWidth = -1;
        private int mScreensizePreHeight = -1;

        private Thread mAdbSendCMDThread;
        private AutoResetEvent mAdbSendCMDEvent = new AutoResetEvent(false);
        private Mutex mAdbCMDMutex = new Mutex();

        //adb exec-out screenrecord --output-format=h264  - | mplayer -cache 512 -
        //adb exec-out screenrecord --size=360x640 --output-format=raw-frames - | mplayer -demuxer rawvideo -rawvideo w = 360:h=640:format=rgb24 -
        private Thread mAdbScrRecThread = null;
        System.Diagnostics.Process mAdbScrRecProc = null;
        private Mutex mAdbScrRecMutex = new Mutex();



        enum RefreshMode
        {
            AUTO = 0,
            MUTUAL,
            DISABLE,
            SCR_REC,
            SCR_REC_Gray
        }

        private RefreshMode mRefreshMode = RefreshMode.SCR_REC;

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

        private FFMpegWrapperCLI mDecoder = new FFMpegWrapperCLI();

        public MainWindow()
        {
            InitializeComponent();

            foreach (var arg in Environment.GetCommandLineArgs())
            {
                Console.WriteLine(arg);
            }
            if (Environment.GetCommandLineArgs().Length >= 2)
                mAdbPath = Environment.GetCommandLineArgs()[1];

            mRefreshMode = (RefreshMode)ADBWrapper.Properties.Settings.Default.RefreshMode;

            if (mRefreshMode != RefreshMode.SCR_REC && mRefreshMode != RefreshMode.SCR_REC_Gray)
                ShowScreenshotFromMemory();

            mAdbSendCMDThread = new Thread(() => {
                ItemCMD adb_cmd = new ItemCMD();
                int queue_size = 0;
                while (!mAdbClosing)
                {
                    mAdbSendCMDEvent.WaitOne();
                    //Console.WriteLine("mAdbSendCMDThread " + mAdbClosing);

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
                
                if (d.Data != null)
                {
                    string msg = d.Data.Trim();
                    if (msg.Length != 0) Console.WriteLine("Error: " + d.Data);
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
                WriteExecption(e.ToString());
                this.Dispatcher.Invoke(() =>
                {
                    UpdateMessage(e.ToString(), MessageLevel.ERROR);
                });
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

                //int val;
                //while ((val = adb_proc.StandardOutput.Read()) != -1)
                //    mem_stream.WriteByte((byte)val);

                char[] chbuffer = new char[128];
                int nRead = 0;
                var encoder = System.Text.Encoding.GetEncoding("latin1");
                do
                {
                    nRead = adb_proc.StandardOutput.Read(chbuffer, 0, chbuffer.Length);
                    byte[] binbuffer = encoder.GetBytes(chbuffer);//chbuffer.Select(c => (byte)c).ToArray();
                    mem_stream.Write(binbuffer, 0, nRead);
                } while (nRead != 0);

                adb_proc.WaitForExit();

                mAdbCMDMutex.ReleaseMutex();
                return adb_proc.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                WriteExecption(e.ToString());
                this.Dispatcher.Invoke(() =>
                {
                    UpdateMessage(e.ToString(), MessageLevel.ERROR);
                });
                mAdbCMDMutex.ReleaseMutex();
                return false;
            }
        }

        //adb exec-out screenrecord --output-format=h264  - | mplayer -cache 512 -
        //adb exec-out screenrecord --size=360x640 --output-format=raw-frames - | mplayer -demuxer rawvideo -rawvideo w = 360:h=640:format=rgb24 -
        //private Thread mAdbScrRecThread = null;
        //System.Diagnostics.Process mAdbScrRecProc = null;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        public static extern void CopyMemory(IntPtr Destination, IntPtr Source, uint Length);

        public static WriteableBitmap FromNativePointer(IntPtr pData, int w, int h, int ch)
        {
            PixelFormat format = PixelFormats.Default;

            if (ch == 1) format = PixelFormats.Gray8; //grey scale image 0-255
            if (ch == 3) format = PixelFormats.Bgr24; //RGB
            if (ch == 4) format = PixelFormats.Bgr32; //RGB + alpha


            WriteableBitmap wbm = new WriteableBitmap(w, h, 96, 96, format, null);
            CopyMemory(wbm.BackBuffer, pData, (uint)(w * h * ch));

            wbm.Lock();
            wbm.AddDirtyRect(new Int32Rect(0, 0, wbm.PixelWidth, wbm.PixelHeight));
            wbm.Unlock();

            return wbm;
        }

        bool StartAdbScrRec()
        {
            int proc_buf_len = 4096*2;
            StopAdbScrRec();
            if (!mDecoder.init(-1, proc_buf_len * 2, true))
            {
                Console.WriteLine("Error: Decode initialization failed.");
                return false;
            }
            mAdbScrRecThread = new Thread(() => {
                string errorMsgPre = "";
                do
                {
                    System.Diagnostics.Process adb_proc = new System.Diagnostics.Process();
                    adb_proc.StartInfo.FileName = mAdbPath;
                    adb_proc.StartInfo.Arguments = "exec-out screenrecord --output-format=h264 -";
                    adb_proc.StartInfo.UseShellExecute = false;

                    adb_proc.StartInfo.StandardOutputEncoding = System.Text.Encoding.GetEncoding("latin1");

                    adb_proc.StartInfo.RedirectStandardInput = true;
                    adb_proc.StartInfo.RedirectStandardOutput = true;
                    adb_proc.StartInfo.RedirectStandardError = true;
                    adb_proc.StartInfo.CreateNoWindow = true;

                    adb_proc.ErrorDataReceived += new System.Diagnostics.DataReceivedEventHandler((o, d) =>
                    {
                        if (d.Data != null)
                        {
                            string msg = d.Data.Trim();
                            if (msg != errorMsgPre)
                            {
                                Console.WriteLine(msg);
                                this.Dispatcher.Invoke(() =>
                                {
                                    UpdateMessage(msg, MessageLevel.ERROR);
                                });
                                errorMsgPre = msg;
                            }
                        }
                    });

                    IntPtr unmanagedBuffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(proc_buf_len);
                    IntPtr unmanagedWidth = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
                    IntPtr unmanagedHeight = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
                    IntPtr unmanagedChannels = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));

                    IntPtr unmanagedImgData = IntPtr.Zero;

                    WriteableBitmap wbm = null;

                    mAdbScrRecMutex.WaitOne();
                    mAdbScrRecProc = adb_proc;
                    mAdbScrRecMutex.ReleaseMutex();

                    this.Dispatcher.Invoke(() =>
                    {
                        RenderOptions.SetBitmapScalingMode(mAdbScreenShot, BitmapScalingMode.LowQuality);
                        mAdbScreenShot.Opacity = 1;
                        mLabelUpdatedInterval.Content = "Waiting for stream to start";
                        mLabelUpdatedIntervalBlur.Content = mLabelUpdatedInterval.Content;
                    });

                    int width = 0, height = 0, channels = mRefreshMode == RefreshMode.SCR_REC_Gray ? 1 : 0;
                    int num_updated = 0;
                    try
                    {

                        adb_proc.Start();
                        //adb_proc.BeginOutputReadLine();
                        adb_proc.BeginErrorReadLine();

                        //int val;
                        //while ((val = adb_proc.StandardOutput.Read()) != -1)
                        //    mem_stream.WriteByte((byte)val);

                        char[] chbuffer = new char[proc_buf_len];
                        int nRead = 0;
                        var encoder = System.Text.Encoding.GetEncoding("latin1");

                        do
                        {
                            try
                            {
                                nRead = adb_proc.StandardOutput.Read(chbuffer, 0, chbuffer.Length);
                            }
                            catch (System.NullReferenceException) {
                                break;
                            }
                            byte[] binbuffer = encoder.GetBytes(chbuffer, 0, nRead);//chbuffer.Select(c => (byte)c).ToArray();
                            System.Runtime.InteropServices.Marshal.Copy(binbuffer, 0, unmanagedBuffer, binbuffer.Length);
                            System.Runtime.InteropServices.Marshal.WriteInt32(unmanagedWidth, width);
                            System.Runtime.InteropServices.Marshal.WriteInt32(unmanagedHeight, height);
                            System.Runtime.InteropServices.Marshal.WriteInt32(unmanagedChannels, channels);


                            int ret_status = -1;
                            ret_status = mDecoder.decoderBuffer(unmanagedBuffer, binbuffer.Length, unmanagedWidth, unmanagedHeight, unmanagedChannels, unmanagedImgData);

                            int w = System.Runtime.InteropServices.Marshal.ReadInt32(unmanagedWidth);
                            int h = System.Runtime.InteropServices.Marshal.ReadInt32(unmanagedHeight);
                            int c = System.Runtime.InteropServices.Marshal.ReadInt32(unmanagedChannels);


                            if (unmanagedImgData != IntPtr.Zero && ret_status == 1)//DECODE_STATUS_OK
                            {
                                
                                try
                                {
                                    this.Dispatcher.Invoke(() =>
                                    {
                                        if (wbm == null)
                                            wbm = FromNativePointer(unmanagedImgData, width, height, channels);
                                        else
                                        {
                                            CopyMemory(wbm.BackBuffer, unmanagedImgData, (uint)(width * height * channels));

                                            wbm.Lock();
                                            wbm.AddDirtyRect(new Int32Rect(0, 0, wbm.PixelWidth, wbm.PixelHeight));
                                            wbm.Unlock();

                                        }
                                        //mAdbScreenShot.Source = bmpsrc;
                                        mAdbScreenShot.Source = wbm;
                                        if ((num_updated % 30) == 0)
                                        {
                                            mLabelUpdatedInterval.Content = String.Format("dec:{0:0.00}ms,cvt:{1:0.00}ms", mDecoder.getPerformance(0) * 1000, mDecoder.getPerformance(1) * 1000);
                                            mLabelUpdatedIntervalBlur.Content = mLabelUpdatedInterval.Content;
                                        }
                                    });
                                }
                                catch (System.Threading.Tasks.TaskCanceledException) { }
                                ++num_updated;
                            }

                            
                            if ((w * h * c > 0) && (w != width || h != height || c != channels))
                            {
                                width = w;
                                height = h;
                                channels = c;

                                if (unmanagedImgData != IntPtr.Zero)
                                    System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanagedImgData);
                                unmanagedImgData = System.Runtime.InteropServices.Marshal.AllocHGlobal(width * height * channels);
                                wbm = null;
                                Console.WriteLine("nread:{0} status:{1} {2} x {3} x {4} {5} NEW!", nRead, ret_status, width, height, channels, num_updated);

                            }
                            else if ((num_updated % 40) == 0)
                                Console.WriteLine("nread:{0} status:{1} {2} x {3} x {4} {5}", nRead, ret_status, width, height, channels, num_updated);
                            //
                        } while (nRead != 0 || !adb_proc.HasExited);
                        Console.WriteLine("RecScr stoped.....");
                        
                    }
                    catch (System.ComponentModel.Win32Exception e)
                    {
                        WriteExecption(e.ToString());
                        this.Dispatcher.Invoke(() =>
                        {
                            UpdateMessage(e.ToString(), MessageLevel.ERROR);
                        });
                    }
                    catch (System.InvalidOperationException e)
                    {
                        WriteExecption(e.ToString());
                        this.Dispatcher.Invoke(() =>
                        {
                            UpdateMessage(e.ToString(), MessageLevel.WARNING);
                        });
                    }
                
                    mAdbScrRecMutex.WaitOne();
                    mAdbScrRecProc = null;
                    mAdbScrRecMutex.ReleaseMutex();

                    System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanagedBuffer);
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanagedWidth);
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanagedHeight);
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanagedChannels);
                    if (unmanagedImgData != IntPtr.Zero)
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanagedImgData);

                    if (errorMsgPre.Contains("no devices/emulators found"))
                        Thread.Sleep(3000);
                } while (mRefreshMode == RefreshMode.SCR_REC || mRefreshMode == RefreshMode.SCR_REC_Gray);
                Console.WriteLine("RecScr thead exited.....");
            });
            mAdbScrRecThread.Start();
            return true;
        }

        bool StopAdbScrRec()
        {
            mAdbScrRecMutex.WaitOne();
            if (mAdbScrRecProc != null)
                mAdbScrRecProc.Close();
            mAdbScrRecMutex.ReleaseMutex();
            return true;
        }

        /*
            ActivityList replacement trick : ActivityList.xml
            <?xml version="1.0" encoding="utf-8"?>
            <Root>
	            <ActivityList>
		            <Activity name="SettingsSysInfo"      cmd="am start -n com.android.settings/.Settings\$SystemDashboardActivity"/>
		            <Activity name="Camera"               cmd="am start -a android.media.action.IMAGE_CAPTURE"/>
	            </ActivityList>
            </Root>
         */
        bool ReadAdbCmdXml(string path = "")
        {
            if (path.Length == 0)
                path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location) + "\\ActivityList.xml";

            try
            {
                XmlDocument XmlDoc = new XmlDocument();
                XmlDoc.Load(path);
                XmlNodeList xmlActivityList = XmlDoc.SelectNodes("Root/ActivityList/Activity");

                if (xmlActivityList.Count > 0)//replace default menu
                {
                    foreach (var i in mContextMenuActivityList.Items)
                    {
                        MenuItem m = i as MenuItem;
                        if (m != null) m.Visibility = Visibility.Collapsed;
                    }
                }

                foreach (XmlNode a in xmlActivityList)
                {
                    Console.WriteLine(a.Attributes["name"].Value + ":" + a.Attributes["cmd"].Value);
                    MenuItem mitem = new MenuItem();
                    mitem.Header = a.Attributes["name"].Value;
                    mitem.Click += BtnSettingsMenuItem_Click;
                    mitem.ToolTip = a.Attributes["cmd"].Value;
                    mContextMenuActivityList.Items.Add(mitem);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                return false;
            }

            return true;
        }

        bool WriteExecption(string exp)
        {
            Console.WriteLine(exp);
            bool res = true;
            exp = exp.Trim();
            if (exp.Length == 0) return true;
            //AppDomain.CurrentDomain.BaseDirectory
            try
            {
                File.AppendAllText(AppDomain.CurrentDomain.BaseDirectory + "execptions.log",
                    DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") +"\n" + exp + "\n\n");
            }catch (Exception e)
            {
                res = false;
                Console.WriteLine(e.ToString());
                this.Dispatcher.Invoke(() =>
                {
                    UpdateMessage(e.ToString(), MessageLevel.ERROR);
                });
            }

            return res;
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
                    WriteExecption("Image format not supported " + e.ToString());
                    this.Dispatcher.Invoke(() =>
                    {
                        UpdateMessage(e.ToString(), MessageLevel.ERROR);
                    });
                }
                catch (System.IO.FileFormatException ffe)
                {
                    WriteExecption("Image format not supported " + ffe.ToString());
                    this.Dispatcher.Invoke(() =>
                    {
                        UpdateMessage(ffe.ToString(), MessageLevel.ERROR);
                    });
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
        
        private int getOrientation()
        {
            MemoryStream mem_stream = new MemoryStream();
            bool res;
            int ori = -1;
            if ((res = RunCMDtoMEM(mAdbPath, "shell \"dumpsys input | grep 'SurfaceOrientation'\"", ref mem_stream)) && mem_stream.Length != 0)
            {
                string resStr = System.Text.Encoding.GetEncoding("latin1").GetString(mem_stream.ToArray());
                Regex regex = new Regex("SurfaceOrientation\\:\\s*(\\d+)", RegexOptions.IgnoreCase);
                MatchCollection matches = regex.Matches(resStr);
                foreach (Match match in matches)
                {
                    GroupCollection groups = match.Groups;
                    if (groups.Count >= 2)
                    {
                        ori = Int32.Parse(groups[1].Value);
                    }
                }
            }
            return ori;
        }

        private void BtnDebug_Click(object sender, RoutedEventArgs e)
        {
            if (sender == mBtnDebug)
            {
                Console.WriteLine("ori " + getOrientation());
            }
            else if (sender == mBtnDebug2)
            {
                //mRefreshMode = RefreshMode.DISABLE;
                //StopAdbScrRec();
            }
        }


        private void BtnPower_Click(object sender, RoutedEventArgs e)
        {
            AdbCMDShell("input keyevent 26");
            if (mRefreshMode == RefreshMode.MUTUAL || mRefreshMode == RefreshMode.DISABLE)
                ShowScreenshotFromMemory();
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            AdbCMDShell("input keyevent 3");
            if (mRefreshMode == RefreshMode.MUTUAL || mRefreshMode == RefreshMode.DISABLE)
                ShowScreenshotFromMemory();
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            AdbCMDShell("input keyevent 4");
            if (mRefreshMode == RefreshMode.MUTUAL || mRefreshMode == RefreshMode.DISABLE)
                ShowScreenshotFromMemory();
        }

        private void BtnScreenshot_Click(object sender, RoutedEventArgs e)
        {
            mBtnScreenshot.IsEnabled = false;
            //if (mRefreshMode != RefreshMode.AUTO)
            ShowScreenshotFromMemory(true);
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
            else if (sender == mMenuItemCamera)
                AdbCMDShell("am start -a android.media.action.IMAGE_CAPTURE");
            else
            {
                MenuItem mitem = sender as MenuItem;
                if (mitem != null)
                {
                    Console.WriteLine("cmd:[" + mitem.ToolTip.ToString() + "]");
                    AdbCMDShell(mitem.ToolTip.ToString());
                }
            }
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
                mImgRefreshRec.Visibility = Visibility.Collapsed;
                mImgRefreshMutually.Visibility = Visibility.Visible;
                StopAdbScrRec();
            }
            else if (mRefreshMode == RefreshMode.AUTO)
            {
                DoubleAnimation da = new DoubleAnimation();
                da.To = 1;
                da.Duration = new Duration(TimeSpan.FromSeconds(0.25));
                mBtnAutoRefresh.BeginAnimation(OpacityProperty, da);

                mImgRefreshAuto.Visibility = Visibility.Visible;
                mImgRefreshRec.Visibility = Visibility.Collapsed;
                mImgRefreshMutually.Visibility = Visibility.Collapsed;
                mAdbSendCMDEvent.Set();
                StopAdbScrRec();
            }
            else if (mRefreshMode == RefreshMode.MUTUAL)
            {
                mImgRefreshAuto.Visibility = Visibility.Collapsed;
                mImgRefreshRec.Visibility = Visibility.Collapsed;
                mImgRefreshMutually.Visibility = Visibility.Visible;
                StopAdbScrRec();
            }
            else if (mRefreshMode == RefreshMode.SCR_REC || mRefreshMode == RefreshMode.SCR_REC_Gray)
            {
                DoubleAnimation da = new DoubleAnimation();
                da.To = 1;
                da.Duration = new Duration(TimeSpan.FromSeconds(0.25));
                mBtnAutoRefresh.BeginAnimation(OpacityProperty, da);
                mImgRefreshAuto.Visibility = Visibility.Collapsed;
                mImgRefreshRec.Visibility = Visibility.Visible;
                mImgRefreshMutually.Visibility = Visibility.Collapsed;
                StartAdbScrRec();
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
                mRefreshMode = RefreshMode.SCR_REC;
            }
            else if (mRefreshMode == RefreshMode.SCR_REC)
            {
                mRefreshMode = RefreshMode.SCR_REC_Gray;
            }
            else if (mRefreshMode == RefreshMode.SCR_REC_Gray)
            {
                mRefreshMode = RefreshMode.MUTUAL;
            }
            ADBWrapper.Properties.Settings.Default.RefreshMode = (int)mRefreshMode;
            ADBWrapper.Properties.Settings.Default.Save();
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
            else if (sender == mMenuItemQSRecScr || sender == mMenuItemQSRecScrGray)
            {
                if (mRefreshMode == RefreshMode.SCR_REC_Gray || mRefreshMode == RefreshMode.SCR_REC)
                {
                    mRefreshMode = sender == mMenuItemQSRecScr ? RefreshMode.SCR_REC : RefreshMode.SCR_REC_Gray;
                    StopAdbScrRec();
                    ADBWrapper.Properties.Settings.Default.RefreshMode = (int)mRefreshMode;
                    ADBWrapper.Properties.Settings.Default.Save();
                    return;
                }
                mRefreshMode = sender == mMenuItemQSRecScr ? RefreshMode.SCR_REC : RefreshMode.SCR_REC_Gray;
            }
            ADBWrapper.Properties.Settings.Default.RefreshMode = (int)mRefreshMode;
            ADBWrapper.Properties.Settings.Default.Save();
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
            {
                if (mRefreshMode == RefreshMode.SCR_REC || mRefreshMode == RefreshMode.SCR_REC_Gray)
                    StopAdbScrRec();
                else
                    ShowScreenshotFromMemory();
            }
            else
            {
                if (Math.Abs(adb_pos.X - mMousePressPosition.X) < 2 &&
                    Math.Abs(adb_pos.Y - mMousePressPosition.Y) < 2 &&
                    diff.TotalMilliseconds < 150)
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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateBtnAutoRefresh();
            ReadAdbCmdXml();
        }


        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            mAdbScrRecMutex.WaitOne();
            if (mAdbScrRecProc != null)
                mAdbScrRecProc.Kill();
            
            mAdbScrRecMutex.ReleaseMutex();
            if (mAdbScrRecThread!=null)
                mAdbScrRecThread.Abort();
            
            mAdbClosing = true;
            mRefreshMode = RefreshMode.DISABLE;
            mAdbSendCMDQueueMutex.WaitOne();
            mAdbSendCMDQueue.Clear();
            mAdbSendCMDQueueMutex.ReleaseMutex();

            mAdbCMDMutex.WaitOne();
            mAdbCMDMutex.ReleaseMutex();

            mAdbSendCMDEvent.Set();
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
                ShowMessage(mRichTextBoxMessage.Opacity < 0.5);
                //DoubleAnimation da = new DoubleAnimation();
                //da.To = mRichTextBoxMessage.Opacity < 0.5 ? 1:0;
                //da.Duration = new Duration(TimeSpan.FromSeconds(0.25));
                //mRichTextBoxMessage.BeginAnimation(OpacityProperty, da);
                //mRichTextBoxMessageBlur.BeginAnimation(OpacityProperty, da);

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

            if (mRichTextBoxMessage.Opacity < 0.5 && msgLevel == MessageLevel.ERROR)
                ShowMessage(true);

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

        private void ShowMessage(bool isShow)
        {
            DoubleAnimation da = new DoubleAnimation();
            da.To = isShow ? 1 : 0;
            da.Duration = new Duration(TimeSpan.FromSeconds(0.25));
            mRichTextBoxMessage.BeginAnimation(OpacityProperty, da);
            mRichTextBoxMessageBlur.BeginAnimation(OpacityProperty, da);
        }
    }
}
