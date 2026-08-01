using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Conduit.App;

/// <summary>Displays the mirrored phone screen. Frames arrive as BGRA and are blitted into a
/// <see cref="WriteableBitmap"/> that resizes to match the phone's actual resolution.</summary>
public partial class ScreenMirrorWindow : Window
{
    private WriteableBitmap? _bitmap;

    public ScreenMirrorWindow() => InitializeComponent();

    /// <summary>Must be called on the UI thread. Updates the displayed frame.</summary>
    public void UpdateFrame(byte[] bgra, int width, int height, int stride)
    {
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            Screen.Source = _bitmap;
            WaitingText.Visibility = Visibility.Collapsed;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, stride, 0);
    }
}
