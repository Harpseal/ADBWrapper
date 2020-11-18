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
using System.Runtime.InteropServices;

namespace ADBWrapper
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("Kernel32")]
        public static extern void AllocConsole();

        [DllImport("Kernel32")]
        public static extern void FreeConsole();

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

        System.Diagnostics.Process mAdbInputProc = null;

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

        public class DeviceInfo
        {
            public string name;
            public string ip;
        }
        private List<DeviceInfo> mDeviceList = null;
        private DeviceInfo mDevice = null;
        private const int TCP_PORT = 5555;


        private Point mMousePressPosition;
        private bool mIsLeftPressed = false;
        private bool mIsRightPressed = false;
        private DateTime mMousePressedTime = DateTime.Now;
        private bool mIsEnableSendEvent = false;

        private DateTime mScreenshotUpdatedTime = DateTime.Now;

        private FFMpegWrapperCLI mDecoder = new FFMpegWrapperCLI();

        public MainWindow()
        {
            InitializeComponent();
            
            var args = Environment.GetCommandLineArgs();
            for (int i=1;i<args.Length;i++)
            {
                if (args[i].EndsWith("adb.exe"))
                    mAdbPath = args[i];
                else if (args[i] == "--console")
                    AllocConsole();
                else if (args[i] == "--wheel" && i + 1 < args.Length)
                {
                    string[] arg_wheel = args[i + 1].Split(',');
                    if (arg_wheel.Length == 4)
                    {
                        try
                        {
                            m_iMouseWheelSkipFrames = Int32.Parse(arg_wheel[0]);
                            m_iMouseWheelDeltaScale = Int32.Parse(arg_wheel[1]);
                            m_iMouseWheelMaxDragDis = Int32.Parse(arg_wheel[2]);
                            m_iMouseWheelDragTimeMS = Int32.Parse(arg_wheel[3]);
                        }
                        catch (FormatException) { };

                    }
                }
            }

            Console.WriteLine("--console --wheel skipframes:{0},deltascale:{1},maxdragdis:{2},dragms:{3}",
                m_iMouseWheelSkipFrames,
                m_iMouseWheelDeltaScale,
                m_iMouseWheelMaxDragDis,
                m_iMouseWheelDragTimeMS);

            mRefreshMode = (RefreshMode)ADBWrapper.Properties.Settings.Default.RefreshMode;

            if (mRefreshMode == RefreshMode.SCR_REC)
                mMenuItemQSRecScr.IsChecked = true;
            else if (mRefreshMode == RefreshMode.SCR_REC_Gray)
                mMenuItemQSRecScrGray.IsChecked = true;
            if (mRefreshMode != RefreshMode.SCR_REC && mRefreshMode != RefreshMode.SCR_REC_Gray)
                ShowScreenshotFromMemory();

            UpdateDeviceList();

            if (mDeviceList != null && mDeviceList.Count>=2)
            {
                List<string> deviceList = new List<string>();
                foreach (var d in mDeviceList)
                {
                    deviceList.Add(d.name);
                }
                SelectDialog selectDialog = new SelectDialog(deviceList, "Please select the desired device");
                selectDialog.ShowDialog();
                if ((bool)selectDialog.DialogResult)
                {
                    int selectedIdx = selectDialog.getSelectedIndex();
                    if (selectedIdx>=1)
                    {
                        mDevice = mDeviceList[selectedIdx];
                        UpdateDeviceList();
                    }
                }
            }




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
                            if (adb_cmd.onCompleted != null)
                            {
                                mScreenshotUpdatedTime = DateTime.Now;
                                MemoryStream mem_stream = new MemoryStream();
                                if ((res = RunCMDtoMEM(adb_cmd.filename, adb_cmd.arguments, ref mem_stream)) && mem_stream.Length != 0)
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
                            adb_cmd_scr.arguments = GetDeviceCmdParam() + "exec-out screencap -p";
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
            //return AdbCMD("shell " + cmd);
            if (mAdbInputProc == null || mAdbInputProc.HasExited)
            {
                mAdbInputProc = new System.Diagnostics.Process();
                mAdbInputProc.StartInfo.FileName = mAdbPath;
                mAdbInputProc.StartInfo.Arguments = GetDeviceCmdParam() + "shell";
                mAdbInputProc.StartInfo.UseShellExecute = false;

                mAdbInputProc.StartInfo.RedirectStandardInput = true;
                mAdbInputProc.StartInfo.RedirectStandardError = true;
                mAdbInputProc.StartInfo.CreateNoWindow = true;

                mAdbInputProc.ErrorDataReceived += new System.Diagnostics.DataReceivedEventHandler((o, d) =>
                {
                    if (d.Data != null)
                    {
                        string msg = d.Data.Trim();
                        Console.WriteLine(msg);
                        this.Dispatcher.Invoke(() =>
                        {
                            UpdateMessage(msg, MessageLevel.ERROR);
                        });
                    }
                });
                mAdbInputProc.Start();
            }
            //Console.WriteLine(cmd);

            mAdbInputProc.StandardInput.WriteLine(cmd);
            return true;
        }
        private bool AdbInputTap(Point pos)
        {
            string tapCmd = "input tap " + (int)pos.X + " " + (int)pos.Y;
            return AdbCMDShell(tapCmd);
        }

        private bool AdbInputSwipe(Point pos0,Point pos1,int duration = 100, bool isKeepCMD = true)
        {
            string swipeCmd = "input swipe " + (int)pos0.X + " " + (int)pos0.Y + " " + (int)pos1.X + " " + (int)pos1.Y + " " + duration;
            return AdbCMDShell(swipeCmd, isKeepCMD);
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
                    if ((uint)e.ErrorCode == 0x80004005)
                    {
                        UpdateMessage("The system cannot find the adb.exe", MessageLevel.ERROR);
                        ShowMessage(true);
                    }
                    else
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
                    if ((uint)e.ErrorCode == 0x80004005)
                    {
                        UpdateMessage("The system cannot find the adb.exe", MessageLevel.ERROR);
                        ShowMessage(true);
                    }
                    else
                        UpdateMessage(e.ToString(), MessageLevel.ERROR);
                });
                mAdbCMDMutex.ReleaseMutex();
                return false;
            }
        }

        private bool RunCMDwithMsgBox(string filename, string arguments, bool isMultiThread = true)
        {
            if (isMultiThread)
            {
                Thread thread = new Thread(new ThreadStart(() => {
                    MemoryStream mem = new MemoryStream();
                    RunCMDtoMEM(filename, arguments, ref mem);
                    MessageBox.Show(Encoding.ASCII.GetString(mem.ToArray()));
                }));
                thread.Start();
            }
            else
            {
                MemoryStream mem = new MemoryStream();
                if (!RunCMDtoMEM(filename, arguments, ref mem))
                    return false;
                MessageBox.Show(Encoding.ASCII.GetString(mem.ToArray()));
            }

            return true;
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

                DateTime timeOriPre = DateTime.Now;

                //Prepare CheckOrientation cmd
                int surfaceOrientation = 0;
                ItemCMD adb_cmd = new ItemCMD();
                adb_cmd.filename = mAdbPath;
                adb_cmd.arguments = GetDeviceCmdParam() + "shell \"dumpsys input | grep 'Surface'\"";
                adb_cmd.onCompleted = delegate (object o)
                {
                    if (o.GetType().Name == "MemoryStream")
                    {
                        int ori = -1;
                        int width = -1;
                        int height = -1;
                        MemoryStream mem = o as MemoryStream;
                        string resStr = System.Text.Encoding.GetEncoding("latin1").GetString(mem.ToArray());
                        Regex regexOri = new Regex("SurfaceOrientation\\:\\s*(\\d+)", RegexOptions.IgnoreCase);
                        Regex regexWitdh = new Regex("SurfaceWidth\\:\\s*(\\d+)", RegexOptions.IgnoreCase);
                        Regex regexHeight = new Regex("SurfaceHeight\\:\\s*(\\d+)", RegexOptions.IgnoreCase);
                        MatchCollection matches;
                        matches = regexOri.Matches(resStr);
                        foreach (Match match in matches)
                        {
                            GroupCollection groups = match.Groups;
                            if (groups.Count >= 2)
                            {
                                ori = Int32.Parse(groups[1].Value);
                            }
                        }
                        matches = regexWitdh.Matches(resStr);
                        foreach (Match match in matches)
                        {
                            GroupCollection groups = match.Groups;
                            if (groups.Count >= 2)
                            {
                                width = Int32.Parse(groups[1].Value);
                            }
                        }
                        matches = regexHeight.Matches(resStr);
                        foreach (Match match in matches)
                        {
                            GroupCollection groups = match.Groups;
                            if (groups.Count >= 2)
                            {
                                height = Int32.Parse(groups[1].Value);
                            }
                        }
                        Console.WriteLine("Surface: {0} {1} {2}", width, height, ori);
                        if (ori != surfaceOrientation && !mDecoder.isRecMP4())
                        {
                            surfaceOrientation = ori;
                            StopAdbScrRec();
                        }
                    }
                };

                do
                {
                    System.Diagnostics.Process adb_proc = new System.Diagnostics.Process();
                    adb_proc.StartInfo.FileName = mAdbPath;
                    adb_proc.StartInfo.Arguments = GetDeviceCmdParam() + "exec-out screenrecord --output-format=h264 -";
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
                        adb_proc.BeginErrorReadLine();

                        ShowScreenshotFromMemory(false, false);//Force updating frame buffer

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
                                bool isCheckOrientation = false;
                                try
                                {
                                    this.Dispatcher.Invoke(() =>
                                    {
                                        isCheckOrientation = mCheckBoxEnableOriDetect.IsChecked == true;
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
                                            string content = "";
                                            if (mDevice != null)
                                                content = mDevice.name + " - ";
                                            content += string.Format("dec:{0:0.00}ms,cvt:{1:0.00}ms", mDecoder.getPerformance(0) * 1000, mDecoder.getPerformance(1) * 1000);
                                            if (mDecoder.isRecMP4())
                                            {
                                                double total_sec = mDecoder.getRecMP4Seconds();
                                                int sec, min, hour;
                                                
                                                sec = (int)total_sec;
                                                min = sec / 60;
                                                sec = sec % 60;
                                                if (min >= 60)
                                                {
                                                    hour = min / 60;
                                                    min = min % 60;
                                                    content += string.Format(",mp4:{0}:{1:00}:{2:00}", hour, min, sec);
                                                }
                                                else
                                                    content += string.Format(",mp4:{0}:{1:00}", min, sec);

                                            }
                                            mLabelUpdatedInterval.Content = content;
                                            mLabelUpdatedIntervalBlur.Content = mLabelUpdatedInterval.Content;
                                        }
                                    });
                                }
                                catch (System.Threading.Tasks.TaskCanceledException) { }
                                ++num_updated;

                                if (isCheckOrientation)
                                {
                                    TimeSpan diff = DateTime.Now - timeOriPre;//
                                    if (diff.TotalSeconds > 3)
                                    {
                                        mAdbSendCMDQueueMutex.WaitOne();
                                        mAdbSendCMDQueue.Enqueue(adb_cmd);
                                        mAdbSendCMDQueueMutex.ReleaseMutex();

                                        mAdbSendCMDEvent.Set();

                                        timeOriPre = DateTime.Now;
                                    }
                                }
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
                            //else if ((num_updated % 40) == 0)
                            //    Console.WriteLine("nread:{0} status:{1} {2} x {3} x {4} {5}", nRead, ret_status, width, height, channels, num_updated);
                            //
                        } while (nRead != 0 || !adb_proc.HasExited);
                        Console.WriteLine("RecScr stoped.....");
                        
                    }
                    catch (System.ComponentModel.Win32Exception e)
                    {
                        WriteExecption(e.ToString());
                        this.Dispatcher.Invoke(() =>
                        {
                            if ((uint)e.ErrorCode == 0x80004005)
                            {   
                                UpdateMessage("The system cannot find the adb.exe", MessageLevel.ERROR);
                                ShowMessage(true);
                            }
                            else
                                UpdateMessage(e.ToString(), MessageLevel.ERROR);
                        });
                        if ((uint)e.ErrorCode == 0x80004005)
                        {
                            mAdbScrRecMutex.WaitOne();
                            mAdbScrRecProc = null;
                            mAdbScrRecMutex.ReleaseMutex();
                            return;
                        }
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

        bool StopAdbScrRec(bool isUpdateUI=true)
        {
            if (isUpdateUI)
            {
                this.Dispatcher.Invoke(() =>
                {
                    BtnScrMenuItem_Click(mMenuItemScrRecStop, null);
                });
            }
            mAdbScrRecMutex.WaitOne();
            try { 
                if (mAdbScrRecProc != null)
                    mAdbScrRecProc.Kill();
            }
            catch (System.InvalidOperationException) { };
            mAdbScrRecMutex.ReleaseMutex();
            return true;
        }

        void RefreshAdbScrRec()
        {
            if (mRefreshMode == RefreshMode.SCR_REC || mRefreshMode == RefreshMode.SCR_REC_Gray)
                StopAdbScrRec();
            else
                ShowScreenshotFromMemory();
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
            if (!File.Exists(path)) return false;
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

                    if (mRefreshMode != RefreshMode.SCR_REC && mRefreshMode != RefreshMode.SCR_REC_Gray)
                    {
                        mLabelUpdatedInterval.Content = (int)diff.TotalMilliseconds + " ms";
                        mLabelUpdatedIntervalBlur.Content = mLabelUpdatedInterval.Content;
                    }


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
            mAdbScreenshotPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshot");
            if (!Directory.Exists(mAdbScreenshotPath))
                Directory.CreateDirectory(mAdbScreenshotPath);

            string screenshot_filename = "screenshot_" + DateTime.Now.ToString("yy_MM_dd_HH_mm_ss") + ".png";
            screenshot_filename = System.IO.Path.Combine(mAdbScreenshotPath, screenshot_filename);

            bool res = false;
            using (FileStream file = new FileStream(screenshot_filename, FileMode.Create, System.IO.FileAccess.Write))
            {
                mem_stream.WriteTo(file);
                res = file.Length != 0;
            }
            return res;
        }

        bool ShowScreenshotFromMemory(bool isSaveFile = false, bool enableFadeOutAni = true)
        {
            if (enableFadeOutAni)
            {
                this.Dispatcher.Invoke(() =>
                {
                    DoubleAnimation da = new DoubleAnimation();
                    da.To = 0.5;
                    da.Duration = new Duration(TimeSpan.FromSeconds(0.5));
                    mAdbScreenShot.BeginAnimation(OpacityProperty, da);
                });
            }


            mScreenshotUpdatedTime = DateTime.Now;

            ItemCMD adb_cmd = new ItemCMD();
            adb_cmd.filename = mAdbPath;
            adb_cmd.arguments = GetDeviceCmdParam() + "exec-out screencap -p";
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
            if (sender == mBtnDebug)
            {
            }
            else if (sender == mBtnDebug2)
            {
            }
        }


        private void BtnPower_Click(object sender, RoutedEventArgs e)
        {
            AdbCMDShell("input keyevent 26");
            if (mRefreshMode == RefreshMode.MUTUAL || mRefreshMode == RefreshMode.DISABLE)
                ShowScreenshotFromMemory();
        }

        private void BtnNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender == mBtnHome)
            {
                AdbCMDShell("input keyevent 3");
                if (mRefreshMode == RefreshMode.MUTUAL || mRefreshMode == RefreshMode.DISABLE)
                    ShowScreenshotFromMemory();
            }
            else if (sender == mBtnBack)
            {
                AdbCMDShell("input keyevent 4");
                if (mRefreshMode == RefreshMode.MUTUAL || mRefreshMode == RefreshMode.DISABLE)
                    ShowScreenshotFromMemory();
            }
            else if (sender == mBtnAppSwitch)
            {
                AdbCMDShell("input keyevent KEYCODE_APP_SWITCH");
                if (mRefreshMode == RefreshMode.MUTUAL || mRefreshMode == RefreshMode.DISABLE)
                    ShowScreenshotFromMemory();
            }

        }//

        private void BtnScreenshot_Click(object sender, RoutedEventArgs e)
        {
            
            if (mRefreshMode == RefreshMode.SCR_REC || mRefreshMode == RefreshMode.SCR_REC_Gray)
            {
                if (mDecoder.isRecMP4())
                    BtnScrMenuItem_Click(mMenuItemScrRecStop, null);
                else
                    BtnScrMenuItem_Click(mMenuItemScrRecStart, null);
            }
            else
            {
                mBtnScreenshot.IsEnabled = false;
                BtnScrMenuItem_Click(mMenuItemScrShot, null);
                ShowScreenshotFromMemory(true);
            }

                //if (mRefreshMode != RefreshMode.AUTO)
                
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
                mMenuItemScrRecStart.Visibility = Visibility.Collapsed;
                mMenuItemScrRecStop.Visibility = Visibility.Collapsed;

                mBtnScreenshot.ToolTip = "Take a screenshot";
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
                mMenuItemScrRecStart.Visibility = Visibility.Collapsed;
                mMenuItemScrRecStop.Visibility = Visibility.Collapsed;
                mBtnScreenshot.ToolTip = "Take a screenshot";
                mAdbSendCMDEvent.Set();
                StopAdbScrRec();
            }
            else if (mRefreshMode == RefreshMode.MUTUAL)
            {
                mImgRefreshAuto.Visibility = Visibility.Collapsed;
                mImgRefreshRec.Visibility = Visibility.Collapsed;
                mImgRefreshMutually.Visibility = Visibility.Visible;
                mMenuItemScrRecStart.Visibility = Visibility.Collapsed;
                mMenuItemScrRecStop.Visibility = Visibility.Collapsed;
                mBtnScreenshot.ToolTip = "Take a screenshot";
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
                mMenuItemScrRecStart.Visibility = Visibility.Visible;
                mMenuItemScrRecStop.Visibility = Visibility.Collapsed;
                mBtnScreenshot.ToolTip = "Record the video stream";
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
            mMenuItemQSRecScr.IsChecked = false;
            mMenuItemQSRecScrGray.IsChecked = false;
            if (sender == mMenuItemQSAuto)
                mRefreshMode = RefreshMode.AUTO;
            else if (sender == mMenuItemQSMutual)
                mRefreshMode = RefreshMode.MUTUAL;
            else if (sender == mMenuItemQSDisable)
                mRefreshMode = RefreshMode.DISABLE;
            else if (sender == mMenuItemQSRecScr || sender == mMenuItemQSRecScrGray)
            {
                MenuItem menuItem = sender as MenuItem;
                if (menuItem != null && menuItem.IsCheckable) menuItem.IsChecked = true;
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

        private bool AdbSendEvent(Point pos, int status)
        {
            string dev_input = mTextBoxInputDev.Text;
            if (!Regex.Match(dev_input, @"event\d+").Success && !Regex.Match(dev_input, @"mouse\d+").Success)
            {
                mTextBoxInputDev.Text = "";
                return false;
            }
            dev_input = "/dev/input/" + dev_input;

            //https://android.googlesource.com/kernel/goldfish/+/android-goldfish-2.6.29/include/linux/input.h
            uint DOWN = 1;
            uint UP = 0;

            uint EV_SYN = 0x0000;
            uint EV_KEY = 0x0001;
            uint EV_ABS = 0x0003;
            
            uint ABS_MT_POSITION_X  = 0x0035;
            uint ABS_MT_POSITION_Y  = 0x0036;
            uint ABS_MT_PRESSURE    = 0x003a;
            uint ABS_MT_TOUCH_MAJOR = 0x0030;
            uint SYN_REPORT         = 0x0000;
            uint ABS_MT_TRACKING_ID = 0x0039;
            uint BTN_TOOL_FINGER    = 0x145;
            uint BTN_TOUCH          = 0x14a;

            int touch_event_id = 1;

            bool isSaveCMD = false;

            /*
             * getevent -p : get command format
             * getevent -l : list current events 
             */

            if (status == 1) //touch
            {
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_ABS, ABS_MT_TRACKING_ID, touch_event_id), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_KEY, BTN_TOUCH, DOWN), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_KEY, BTN_TOOL_FINGER, DOWN), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_ABS, ABS_MT_POSITION_X, (int)pos.X), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_ABS, ABS_MT_POSITION_Y, (int)pos.Y), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_ABS, ABS_MT_PRESSURE, 5), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_ABS, ABS_MT_TOUCH_MAJOR, 5), isSaveCMD);
            }
            else if (status == 2) //drag, moving
            {
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_ABS, ABS_MT_POSITION_X, (int)pos.X), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_ABS, ABS_MT_POSITION_Y, (int)pos.Y), isSaveCMD);
            }
            else if (status == 3)// untouch
            {
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_ABS, ABS_MT_TRACKING_ID, -1), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_KEY, BTN_TOUCH, UP), isSaveCMD);
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_KEY, BTN_TOOL_FINGER, UP), isSaveCMD);
            }

            if (status != 0)
                AdbCMDShell(String.Format("sendevent {0} {1} {2} {3}", dev_input, EV_SYN, SYN_REPORT, 0), isSaveCMD);

            return true;
        }

        private void AdbScreenShot_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Point pos = e.GetPosition(mAdbScreenShot);
            Point adb_pos = new Point(pos.X * mAdbScreenShot.Source.Width / mAdbScreenShot.ActualWidth,
                pos.Y * mAdbScreenShot.Source.Height / mAdbScreenShot.ActualHeight);

            mMousePressPosition = adb_pos;
            mIsLeftPressed = e.LeftButton == MouseButtonState.Pressed;
            mIsRightPressed = e.RightButton == MouseButtonState.Pressed;

            mMousePressedTime = DateTime.Now;

            if (mIsLeftPressed) mIsEnableSendEvent = AdbSendEvent(adb_pos, 1);//touch
            Console.WriteLine("Down R{0} L{1}", mIsRightPressed, mIsLeftPressed);
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
                {
                    if (!mDecoder.isRecMP4())
                        StopAdbScrRec();
                }
                else
                    ShowScreenshotFromMemory();
            }
            else
            {
                if (!mIsEnableSendEvent)
                {
                    if (Math.Abs(adb_pos.X - mMousePressPosition.X) < 2 &&
                        Math.Abs(adb_pos.Y - mMousePressPosition.Y) < 2 &&
                        diff.TotalMilliseconds < 150)
                        AdbInputTap(mMousePressPosition);
                    else
                        AdbInputSwipe(mMousePressPosition, adb_pos, (int)diff.TotalMilliseconds);
                }
                else if (mIsLeftPressed) AdbSendEvent(adb_pos, 3);//untouch

                if (mRefreshMode == RefreshMode.MUTUAL)
                    ShowScreenshotFromMemory();
            }
            //mIsLeftPressed = mIsRightPressed;
            mIsLeftPressed = e.LeftButton == MouseButtonState.Pressed;
            mIsRightPressed = e.RightButton == MouseButtonState.Pressed;
        }

        private int m_iMouseWheelSkipFrames = -1;//15;
        private int m_iMouseWheelDeltaScale = 25;//25;
        private int m_iMouseWheelMaxDragDis = 400;//400;
        private int m_iMouseWheelDragTimeMS = 100;//100;

        private DateTime m_iMouseWheelPre = DateTime.Now;
        private int m_iMouseWheelDeltaSum = 0;
        private void AdbScreenShot_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var diff = DateTime.Now - m_iMouseWheelPre;
            if (m_iMouseWheelSkipFrames>0)
            {
                if ((mMouseMoveSkipCount++ % m_iMouseWheelSkipFrames) == 0)
                {
                    Point pos = e.GetPosition(mAdbScreenShot);
                    Point adb_pos = new Point(pos.X * mAdbScreenShot.Source.Width / mAdbScreenShot.ActualWidth,
                        pos.Y * mAdbScreenShot.Source.Height / mAdbScreenShot.ActualHeight);
                    int dis = e.Delta * m_iMouseWheelDeltaScale;
                    int max_dis = m_iMouseWheelMaxDragDis;
                    if (dis > max_dis) dis = max_dis;
                    if (dis < -max_dis) dis = -max_dis;
                    Point adb_pos1 = new Point(adb_pos.X, adb_pos.Y + dis);
                    AdbInputSwipe(adb_pos, adb_pos1, m_iMouseWheelDragTimeMS, false);
                    Console.WriteLine(e.Delta + " " + dis);
                }
            }
            else
            {
                
                if (diff.TotalMilliseconds > m_iMouseWheelDragTimeMS)
                {
                    if (m_iMouseWheelDeltaSum != 0)
                    {
                        if (Math.Abs(m_iMouseWheelDeltaSum) < m_iMouseWheelDeltaScale)
                            m_iMouseWheelDeltaSum = m_iMouseWheelDeltaSum > 0 ? m_iMouseWheelDeltaScale : -m_iMouseWheelDeltaScale;
                        Point pos = e.GetPosition(mAdbScreenShot);
                        Point adb_pos = new Point(pos.X * mAdbScreenShot.Source.Width / mAdbScreenShot.ActualWidth,
                            pos.Y * mAdbScreenShot.Source.Height / mAdbScreenShot.ActualHeight);
                        Point adb_pos1 = new Point(adb_pos.X, adb_pos.Y + m_iMouseWheelDeltaSum);
                        AdbInputSwipe(adb_pos, adb_pos1, m_iMouseWheelDragTimeMS, false);
                        Console.WriteLine(e.Delta + " " + m_iMouseWheelDeltaSum);
                        m_iMouseWheelDeltaSum = 0;
                    }
                    m_iMouseWheelPre = DateTime.Now;
                }
                else
                {
                    if (m_iMouseWheelDeltaSum == 0)
                        m_iMouseWheelPre = DateTime.Now;
                    m_iMouseWheelDeltaSum += e.Delta;
                }
            }
            Console.WriteLine(diff.TotalMilliseconds + " " + m_iMouseWheelDeltaSum);
        }


        int mMouseMoveSkipCount = 0;
        private void AdbScreenShot_MouseMove(object sender, MouseEventArgs e)
        {
            Point pos = e.GetPosition(mAdbScreenShot);
            Point adb_pos = new Point(pos.X * mAdbScreenShot.Source.Width / mAdbScreenShot.ActualWidth,
                pos.Y * mAdbScreenShot.Source.Height / mAdbScreenShot.ActualHeight);

            this.Title = Application.Current.MainWindow.GetType().Assembly.GetName().Name + String.Format(" - {0:0.},{1:0.} @ {2}x{3}", adb_pos.X, adb_pos.Y, mAdbScreenShot.Source.Width, mAdbScreenShot.Source.Height);
            //Console.WriteLine(Application.Current.MainWindow.GetType().Assembly.GetName().Name + " " + adb_pos.ToString());
            if (mIsLeftPressed && mIsEnableSendEvent && (++mMouseMoveSkipCount % 5) == 0) AdbSendEvent(adb_pos, 2);//touch
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            mTextBoxInputDev.Text = ADBWrapper.Properties.Settings.Default.InputDev;
            UpdateBtnAutoRefresh();
            ReadAdbCmdXml();
        }


        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            mAdbScrRecMutex.WaitOne();
            try
            {
                if (mAdbScrRecProc != null && !mAdbScrRecProc.HasExited)
                    mAdbScrRecProc.Kill();
            }
            catch (System.InvalidOperationException) { };

            mAdbScrRecMutex.ReleaseMutex();

            if (mAdbScrRecThread!=null)
                mAdbScrRecThread.Abort();

            if (mAdbInputProc != null && !mAdbInputProc.HasExited)
                mAdbInputProc.Kill();

            mAdbClosing = true;
            mRefreshMode = RefreshMode.DISABLE;
            mAdbSendCMDQueueMutex.WaitOne();
            mAdbSendCMDQueue.Clear();
            mAdbSendCMDQueueMutex.ReleaseMutex();

            //mAdbCMDMutex.WaitOne();
            //mAdbCMDMutex.ReleaseMutex();

            mAdbSendCMDEvent.Set();

            if (ADBWrapper.Properties.Settings.Default.InputDev != mTextBoxInputDev.Text.ToString())
            {
                ADBWrapper.Properties.Settings.Default.InputDev = mTextBoxInputDev.Text;
                ADBWrapper.Properties.Settings.Default.Save();
            }
        }

        private void BtnScrMenuItem_Click(object sender, RoutedEventArgs e)
        {
            mAdbScreenshotPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshot");
            if (!Directory.Exists(mAdbScreenshotPath))
                Directory.CreateDirectory(mAdbScreenshotPath);

            if (sender == mMenuItemScrOpenPath)
            {
                System.Diagnostics.Process.Start("explorer.exe", mAdbScreenshotPath);
            }
            else if (sender == mMenuItemScrShot)
            {
                mBtnScreenshot.IsEnabled = false;
                //if (mRefreshMode != RefreshMode.AUTO)
                ShowScreenshotFromMemory(true);
            }
            else if (sender == mMenuItemScrRecStart)
            {
                bool isStartRec = false;
                string screenshot_filename = "screenrecord_" + DateTime.Now.ToString("yy_MM_dd_HH_mm_ss") + ".mp4";
                screenshot_filename = System.IO.Path.Combine(mAdbScreenshotPath, screenshot_filename);

                Console.WriteLine("WPF:"+screenshot_filename);
                isStartRec = mDecoder.startRecMP4(screenshot_filename);

                if (isStartRec)
                {
                    StopAdbScrRec(false);
                    mMenuItemScrRecStart.Visibility = Visibility.Collapsed;
                    mMenuItemScrRecStop.Visibility = Visibility.Visible;
                    mImgScrRecStop.Visibility = Visibility.Visible;
                    mImgScrShot.Visibility = Visibility.Collapsed;
                }
            }
            else if (sender == mMenuItemScrRecStop)
            {
                mDecoder.stopRecMP4();
                mMenuItemScrRecStop.Visibility = Visibility.Collapsed;
                mMenuItemScrRecStart.Visibility = Visibility.Visible;
                mImgScrShot.Visibility = Visibility.Visible;
                mImgScrRecStop.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnPowerMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender == mMenuItemReboot)
                AdbCMD(GetDeviceCmdParam() + "reboot");
            else if (sender == mMenuItemKillServer)
                AdbCMD("kill-server");
            else if (sender == mMenuItemSU)
                AdbCMDShell("su",false);
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

            if (mCheckBoxShowMsgIfError.IsChecked == true && mRichTextBoxMessage.Opacity < 0.5 && msgLevel == MessageLevel.ERROR)
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

        private void RefreshInputSelector()
        {
            ItemCMD adb_cmd_getinput = new ItemCMD();
            adb_cmd_getinput.filename = mAdbPath;
            adb_cmd_getinput.arguments = GetDeviceCmdParam() + "shell getevent -p";
            adb_cmd_getinput.onCompleted = delegate (object o)
            {
                if (o.GetType().Name == "MemoryStream")
                {
                    MemoryStream mem = o as MemoryStream;
                    if (mem != null && mem.Length != 0)
                    {
                        string[] cmd_lines = Encoding.ASCII.GetString(mem.ToArray()).Split(
                            new[] { "\r\n", "\r", "\n" },
                            StringSplitOptions.None
                        );

                        Regex regexEvent = new Regex("/dev/input/event\\d+");
                        MatchCollection matches;
                        Dictionary<string, string> dictInput = new Dictionary<string, string>();

                        void addDict(Dictionary<string, string> dict, string key, string[] lines, int idx_start, int idx_end){
                            string value = "";
                            for (int l = idx_start; l < idx_end; l++)
                            {
                                value += lines[l].Trim();
                                if (l + 1 < idx_end) value += Environment.NewLine;
                            }
                            dict[key] = value.Trim();
                        };

                        string dev_name = "";
                        int dev_idx = -1;
                        for (int i = 0; i < cmd_lines.Length; i++)
                        {
                            matches = regexEvent.Matches(cmd_lines[i]);
                            
                            if (matches.Count != 0 && matches[0].Groups.Count > 0)
                            {
                                if (dev_idx >= 0) addDict(dictInput, dev_name, cmd_lines, dev_idx, i);
                                GroupCollection groups = matches[0].Groups;
                                dev_name = groups[0].Value;
                                dev_idx = i;
                            }
                            Console.WriteLine("{0}={2} {3} {4} : {1}", i, cmd_lines[i], matches.Count,dev_idx,dev_name);
                        }
                        if (dev_idx >= 0) addDict(dictInput, dev_name, cmd_lines, dev_idx, cmd_lines.Length);

                        if (dictInput.Count > 0)
                        {
                            this.Dispatcher.Invoke(() =>
                            {
                                var l = dictInput.OrderBy(key => key.Key);
                                var dic = l.ToDictionary((keyItem) => keyItem.Key, (valueItem) => valueItem.Value);

                                SortedDictionary<string, string> dictInputSorted = new SortedDictionary<string, string>(dictInput);
                                MenuItem item;
                                mMenuItemInputSelector.Items.Clear();
                                foreach (var p in dictInputSorted)
                                {
                                    item = new MenuItem();
                                    item.Header = p.Key;
                                    item.ToolTip = p.Value;
                                    item.Click += (s,x)=> {
                                        MenuItem clickedItem = (MenuItem)s;
                                        if (clickedItem != null)
                                        {
                                            Console.WriteLine("click " + clickedItem.Header.ToString());
                                            mTextBoxInputDev.Text = clickedItem.Header.ToString().Replace("/dev/input/", "");
                                        }
                                    };
                                    mMenuItemInputSelector.Items.Add(item);
                                }
                                mMenuItemInputSelector.Items.Add(new Separator());
                                item = new MenuItem();
                                item.Header = "Refresh list";
                                item.Click += (s, x) => {
                                    RefreshInputSelector();
                                };
                                mMenuItemInputSelector.Items.Add(item);
                            });
                        }
                    }
                }
            };
            mAdbSendCMDQueueMutex.WaitOne();
            mAdbSendCMDQueue.Enqueue(adb_cmd_getinput);
            mAdbSendCMDQueueMutex.ReleaseMutex();

            mAdbSendCMDEvent.Set();
        }

        private List<DeviceInfo> GetDeviceList()
        {
            MemoryStream mem_stream = new MemoryStream();
            if (RunCMDtoMEM(mAdbPath, "devices", ref mem_stream) && mem_stream.Length != 0)
            {
                string[] cmd_lines = Encoding.ASCII.GetString(mem_stream.ToArray()).Split(
                    new[] { "\r\n", "\r", "\n" },
                    StringSplitOptions.None
                );

                Regex regexDevice = new Regex("^(\\S+)\\s+device");
                Regex regexIP = new Regex("inet\\s+(\\d+\\.\\d+\\.\\d+\\.\\d+)");
                MatchCollection matches;
                var res_list = new List<DeviceInfo>();
                for (int i = 0; i < cmd_lines.Length; i++)
                {
                    matches = regexDevice.Matches(cmd_lines[i]);

                    if (matches.Count != 0 && matches[0].Groups.Count >= 2)
                    {
                        GroupCollection groups = matches[0].Groups;
                        DeviceInfo dev = new DeviceInfo();
                        dev.name = groups[1].Value;
                        dev.ip = dev.name;
                        //adb -s CB5A1LMBP9 shell ip -f inet addr show wlan0

                        System.Net.IPAddress ip;
                        if (!System.Net.IPAddress.TryParse(dev.ip, out ip))
                        {
                            dev.ip = "";
                            mem_stream = new MemoryStream();
                            if (RunCMDtoMEM(mAdbPath, "-s " + dev.name + " shell ip -f inet addr show wlan0", ref mem_stream) && mem_stream.Length != 0)
                            {
                                string[] ip_lines = Encoding.ASCII.GetString(mem_stream.ToArray()).Split(
                                    new[] { "\r\n", "\r", "\n" },
                                    StringSplitOptions.None
                                );

                                for (int j = 0; j < ip_lines.Length; j++)
                                {
                                    MatchCollection matchesIP = regexIP.Matches(ip_lines[j]);
                                    if (matchesIP.Count != 0 && matchesIP[0].Groups.Count >= 2)
                                    {
                                        dev.ip = matchesIP[0].Groups[1].Value + ":" + TCP_PORT.ToString();
                                    }
                                }
                            }
                        }
                        res_list.Add(dev);
                    }
                    Console.WriteLine("{0}={2} : {1}", i, cmd_lines[i], matches.Count);
                }

                return res_list.Count > 0 ? res_list : null;
            }
            return null;
        }

        private string GetDeviceCmdParam()
        {
            if (mDevice != null) return " -s " + mDevice.name + " ";
            return "";
        }
        private void UpdateDeviceList()
        {
            mDeviceList = GetDeviceList();
            bool isDeviceFound = false;
            if (mDeviceList != null)
            {
                foreach (var d in mDeviceList)
                {
                    if (mDevice != null && mDevice.name == d.name)
                    {
                        isDeviceFound = true;
                        break;
                    }
                }
            }
            if (!isDeviceFound) mDevice = null;
            if (mDevice == null && mDeviceList != null && mDeviceList.Count > 0)
            {
                mDevice = mDeviceList[0];
                RefreshAdbScrRec();
            }

            this.Dispatcher.Invoke(() =>
            {
                mMenuItemMenuDevice.Items.Clear();
                if (mDeviceList != null)
                {
                    foreach (var d in mDeviceList)
                    {
                        var item = new MenuItem();
                        item.Header = d.name;
                        mMenuItemMenuDevice.Items.Add(item);

                        MenuItem itemSub;
                        itemSub = new MenuItem() { Header = mDevice != null && mDevice.name == d.name ? "Reselect " + d.name : "Select " + d.name };
                        itemSub.Click += (s, x) =>
                        {
                            mDevice = d;
                            RefreshAdbScrRec();
                            UpdateDeviceList();
                        };
                        itemSub.IsCheckable = true;
                        itemSub.IsChecked = (mDevice.name == d.name);
                        Console.WriteLine(mDevice.name + " => " + d.name);
                        item.Items.Add(itemSub);
                        item.Items.Add(new Separator());

                        itemSub = new MenuItem() { Header = "Enable tcpip" };
                        itemSub.Click += (s, x) =>
                        {
                            string param = "-s " + d.name + " tcpip " + TCP_PORT.ToString();
                            Console.WriteLine(param);
                            RunCMDwithMsgBox(mAdbPath, param);
                        };
                        item.Items.Add(itemSub);

                        if (d.name == d.ip)
                        {
                            itemSub = new MenuItem() { Header = "Disconnect " + d.ip };
                            itemSub.Click += (s, x) =>
                            {
                                string param = "disconnect " + d.ip;
                                Console.WriteLine(param);
                                RunCMDwithMsgBox(mAdbPath, param, false);
                                UpdateDeviceList();
                            };
                            item.Items.Add(itemSub);
                        }
                        else
                        {
                            itemSub = new MenuItem() { Header = "Connect " + d.ip };
                            itemSub.Click += (s, x) =>
                            {
                                string param = "connect " + d.ip;
                                Console.WriteLine(param);
                                RunCMDwithMsgBox(mAdbPath, param, false);
                                UpdateDeviceList();
                            };
                            item.Items.Add(itemSub);
                        }


                        itemSub = new MenuItem() { Header = "Connect USB" };
                        itemSub.Click += (s, x) =>
                        {
                            string param = "-s " + d.name + " usb";
                            Console.WriteLine(param);
                            RunCMDwithMsgBox(mAdbPath, param);
                        };
                        item.Items.Add(itemSub);

                        //itemSub = new MenuItem() { Header = "Remove "+d.name };
                        //itemSub.Click += (s, x) => {
                        //    int idx = -1;
                        //    foreach (MenuItem i in mMenuItemMenuDevice.Items)
                        //    {
                        //        if (i.Header.ToString() == d.name)
                        //        {
                        //            idx = mMenuItemMenuDevice.Items.IndexOf(i);
                        //            break;
                        //        }
                        //    }
                        //    if (idx >= 0) mMenuItemMenuDevice.Items.RemoveAt(idx);
                        //};
                        //item.Items.Add(itemSub);

                        item.Items.Add(new Separator());
                        item.Items.Add(new MenuItem() { Header = d.ip });

                    }

                    if (mDeviceList.Count > 0)
                        mMenuItemMenuDevice.Items.Add(new Separator());
                }
 
                var itemFunc = new MenuItem() { Header = "Refresh list"};
                itemFunc.Click += (s, x) => {
                    UpdateDeviceList();
                };
                mMenuItemMenuDevice.Items.Add(itemFunc);

                itemFunc = new MenuItem() { Header = "Connect to IP..." };
                itemFunc.Click += (s, x) => {
                    uint[] ip = new uint[5] { 192, 168, 99, 1, TCP_PORT };
                    if (mDevice != null)
                    {
                        Regex regexIP = new Regex("(\\d+)\\.(\\d+)\\.(\\d+)\\.(\\d+):(\\d+)");
                        var match = regexIP.Match(mDevice.ip);
                        if (match.Groups.Count == 6)
                        {
                            for (int i = 0; i < 5; i++)
                                ip[i] = System.Convert.ToUInt32(match.Groups[i + 1].ToString());
                        }
                    }
                    SelectDialog selectDialog = new SelectDialog(ip[0], ip[1], ip[2], ip[3], ip[4], "Please input ip");
                    selectDialog.ShowDialog();
                    if ((bool)selectDialog.DialogResult)
                    {
                        string ip_str = selectDialog.getIP();
                        Console.WriteLine("IP:" + ip_str);
                        if (ip_str == null || ip_str.Length == 0)
                        {
                            MessageBox.Show("Invaild IP", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        else
                        {
                            string param = "connect " + ip_str;
                            Console.WriteLine(param);
                            RunCMDwithMsgBox(mAdbPath, param, false);
                            UpdateDeviceList();
                        }
                    }
                };
                mMenuItemMenuDevice.Items.Add(itemFunc);
            });
        }
        private void MenuItemInputSelector_MouseEnter(object sender, MouseEventArgs e)
        {
            if (mMenuItemInputSelector.Items.Count == 0)
            {
                RefreshInputSelector();
            }
        }

        private void BtnMenu_Click(object sender, RoutedEventArgs e)
        {
            //adb shell ip -f inet addr show
            mBtnMenu.ContextMenu.IsOpen = true;
        }
    }
}
