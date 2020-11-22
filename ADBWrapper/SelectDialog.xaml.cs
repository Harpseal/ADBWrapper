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
using System.Windows.Shapes;

namespace ADBWrapper
{
    /// <summary>
    /// Interaction logic for SelectDialog.xaml
    /// </summary>
    public partial class SelectDialog : Window
    {
        public SelectDialog()
        {
            InitializeComponent();
        }

        private List<string> mSelectionList = null;
        private List<RadioButton> mRadioBtnList = null;
        public SelectDialog(List<string> selectionList, string message, string textBtnOK = null, string textBtnCancel = null)
        {
            InitializeComponent();

            mSelectionList = selectionList;
            mRadioBtnList = new List<RadioButton>();
            for (int i=0;i<selectionList.Count;i++)
            {
                RadioButton btn;
                btn = new RadioButton();
                btn.Content = selectionList[i];
                btn.GroupName = "devices";
                if (i == 0) btn.IsChecked = true;
                mPanelDeviceSelection.Children.Add(btn);
                mRadioBtnList.Add(btn);
            }

            mPanelDeviceSelection.Visibility = Visibility.Visible;
            mPanelIP.Visibility = Visibility.Collapsed;
            mPanelResolution.Visibility = Visibility.Collapsed;

            mTextMessage.Text = message;
            if (textBtnOK != null) mBtnOk.Content = textBtnOK;
            if (textBtnCancel != null) mBtnCancel.Content = textBtnCancel;
        }

        public SelectDialog(uint ip0, uint ip1, uint ip2, uint ip3, uint port, string message, string textBtnOK = null, string textBtnCancel = null)
        {
            InitializeComponent();
            mTextIP0.Text = ip0.ToString();
            mTextIP1.Text = ip1.ToString();
            mTextIP2.Text = ip2.ToString();
            mTextIP3.Text = ip3.ToString();
            mTextPort.Text = port.ToString();

            mPanelDeviceSelection.Visibility = Visibility.Collapsed;
            mPanelIP.Visibility = Visibility.Visible;
            mPanelResolution.Visibility = Visibility.Collapsed;

            mTextMessage.Text = message;
            if (textBtnOK != null) mBtnOk.Content = textBtnOK;
            if (textBtnCancel != null) mBtnCancel.Content = textBtnCancel;
        }

        public SelectDialog(uint width, uint height, string message, string textBtnOK = null, string textBtnCancel = null)
        {
            InitializeComponent();
            mTextResWidth.Text = width.ToString();
            mTextResHeight.Text = height.ToString();

            mPanelDeviceSelection.Visibility = Visibility.Collapsed;
            mPanelIP.Visibility = Visibility.Collapsed;
            mPanelResolution.Visibility = Visibility.Visible;

            mTextMessage.Text = message;
            if (textBtnOK != null) mBtnOk.Content = textBtnOK;
            if (textBtnCancel != null) mBtnCancel.Content = textBtnCancel;
        }

        private void Btn_Click(object sender, RoutedEventArgs e)
        {
            if (sender == mBtnOk)
            {
                this.DialogResult = true;
            }
            else if (sender == mBtnCancel)
            {
                this.DialogResult = false;
            }
        }

        public int getSelectedIndex()
        {
            if (mSelectionList == null || mRadioBtnList == null) return -1;
            foreach (var b in mRadioBtnList)
            {
                if (b.IsChecked == true)
                {
                    return mSelectionList.IndexOf(b.Content.ToString());
                }
            }
            return -1;
        }

        public string getIP()
        {
            int n;
            if (int.TryParse(mTextIP0.Text, out n) && n >= 0 && n < 256 &&
                int.TryParse(mTextIP1.Text, out n) && n >= 0 && n < 256 &&
                int.TryParse(mTextIP2.Text, out n) && n >= 0 && n < 256 &&
                int.TryParse(mTextIP3.Text, out n) && n >= 0 && n < 256 &&
                int.TryParse(mTextPort.Text, out n) && n >= 0 && n < 65536)
            {
                return string.Format("{0}.{1}.{2}.{3}:{4}", mTextIP0.Text, mTextIP1.Text, mTextIP2.Text, mTextIP3.Text, mTextPort.Text);
            }
            return "";
        }

        public string getResolution()
        {
            int n;
            if (int.TryParse(mTextResWidth.Text, out n) && n > 0 &&
                int.TryParse(mTextResHeight.Text, out n) && n > 0)
            {
                return string.Format("{0}x{1}", mTextResWidth.Text, mTextResHeight.Text);
            }
            return "";
        }
    }
}
