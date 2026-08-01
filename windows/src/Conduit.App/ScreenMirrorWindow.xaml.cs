using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Conduit.App;

/// <summary>
/// Displays the mirrored phone screen and captures mouse/keyboard so the PC can drive the phone.
/// Frames arrive as BGRA into a <see cref="WriteableBitmap"/> that resizes to the phone's actual
/// resolution. Clicks/drags/wheel and typed text are surfaced as normalized (0..1) input events.
/// </summary>
public partial class ScreenMirrorWindow : Window
{
    private WriteableBitmap? _bitmap;
    private Point _downPos;
    private DateTime _downTime;
    private bool _dragging;

    // Normalized (0..1) input events raised for the ScreenMirrorService to forward to the phone.
    public event Action<double, double>? Tapped;
    public event Action<double, double, double, double, int>? Swiped;
    public event Action<string>? KeyPressed;
    public event Action<string>? TextTyped;

    public ScreenMirrorWindow()
    {
        InitializeComponent();
        Screen.MouseLeftButtonDown += OnMouseDown;
        Screen.MouseLeftButtonUp += OnMouseUp;
        Screen.MouseWheel += OnMouseWheel;
        Loaded += (_, _) => Focus();
    }

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

    // ---- Mouse → tap / swipe --------------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _downPos = e.GetPosition(Screen);
        _downTime = DateTime.UtcNow;
        _dragging = true;
        Screen.CaptureMouse();
        Focus(); // so typing goes to us afterwards
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Screen.ReleaseMouseCapture();

        var up = e.GetPosition(Screen);
        var from = ToNormalized(_downPos);
        var to = ToNormalized(up);
        if (from is null || to is null) return;

        double dist = (up - _downPos).Length;
        if (dist < 10)
        {
            Tapped?.Invoke(from.Value.X, from.Value.Y);
        }
        else
        {
            int ms = (int)Math.Clamp((DateTime.UtcNow - _downTime).TotalMilliseconds, 40, 2000);
            Swiped?.Invoke(from.Value.X, from.Value.Y, to.Value.X, to.Value.Y, ms);
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // A wheel notch becomes a short vertical swipe centered on screen (scrolls the phone).
        double y1 = e.Delta < 0 ? 0.60 : 0.40;
        double y2 = e.Delta < 0 ? 0.35 : 0.65;
        Swiped?.Invoke(0.5, y1, 0.5, y2, 60);
    }

    // ---- Keyboard → text / keys ----------------------------------------------

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (!string.IsNullOrEmpty(e.Text))
        {
            TextTyped?.Invoke(e.Text);
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        string? key = e.Key switch
        {
            Key.Back => "backspace",
            Key.Enter => "enter",
            Key.Escape => "back",
            _ => null,
        };
        if (key is not null)
        {
            KeyPressed?.Invoke(key);
            e.Handled = true;
        }
    }

    // ---- Coordinate mapping ---------------------------------------------------

    /// <summary>Maps a point in the Image control to normalized (0..1) image coords, honoring the
    /// Uniform-stretch letterbox. Null if the point is outside the actual displayed image.</summary>
    private Point? ToNormalized(Point p)
    {
        if (_bitmap is null) return null;
        double aw = Screen.ActualWidth, ah = Screen.ActualHeight;
        if (aw <= 0 || ah <= 0) return null;

        double bw = _bitmap.PixelWidth, bh = _bitmap.PixelHeight;
        double scale = Math.Min(aw / bw, ah / bh);
        double rw = bw * scale, rh = bh * scale;
        double ox = (aw - rw) / 2, oy = (ah - rh) / 2;

        double x = (p.X - ox) / rw, y = (p.Y - oy) / rh;
        if (x < 0 || x > 1 || y < 0 || y > 1) return null;
        return new Point(x, y);
    }
}
