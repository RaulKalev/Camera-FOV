using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Camera_FOV
{
    public partial class TitleBar : UserControl
    {
        public static readonly DependencyProperty CanMinimizeProperty = DependencyProperty.Register(
            nameof(CanMinimize), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

        public TitleBar()
        {
            InitializeComponent();
        }

        public bool CanMinimize
        {
            get => (bool)GetValue(CanMinimizeProperty);
            set => SetValue(CanMinimizeProperty, value);
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Window.GetWindow(this)?.DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.WindowState = WindowState.Minimized;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }
    }
}
