using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PCL;

/// <summary>
/// 精灵表动画控件：将一张竖排分帧的透明 PNG（frameOrder = TopToBottom）逐帧播放。
/// 内部只用一张 BitmapSource，通过裁剪(Clip) + 缩放(RenderTransform) 逐帧呈现，性能友好。
/// </summary>
public partial class SpriteSheetAnimation : UserControl
{
    private readonly DispatcherTimer _timer;
    private int _frameIndex;
    private bool _rebuilding;

    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource), typeof(ImageSource), typeof(SpriteSheetAnimation),
        new PropertyMetadata(null, OnRequireRebuild));

    public static readonly DependencyProperty FrameWidthProperty = DependencyProperty.Register(
        nameof(FrameWidth), typeof(double), typeof(SpriteSheetAnimation),
        new PropertyMetadata(256d, OnRequireRebuild));

    public static readonly DependencyProperty FrameHeightProperty = DependencyProperty.Register(
        nameof(FrameHeight), typeof(double), typeof(SpriteSheetAnimation),
        new PropertyMetadata(128d, OnRequireRebuild));

    public static readonly DependencyProperty FrameCountProperty = DependencyProperty.Register(
        nameof(FrameCount), typeof(int), typeof(SpriteSheetAnimation),
        new PropertyMetadata(28, OnRequireRebuild));

    public static readonly DependencyProperty FpsProperty = DependencyProperty.Register(
        nameof(Fps), typeof(double), typeof(SpriteSheetAnimation),
        new PropertyMetadata(24d, OnFpsChanged));

    /// <summary>显示缩放：最终显示尺寸 = 单帧尺寸 × Scale。</summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale), typeof(double), typeof(SpriteSheetAnimation),
        new PropertyMetadata(0.45d, OnRequireRebuild));

    public SpriteSheetAnimation()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000d / Fps) };
        _timer.Tick += (_, _) => Advance();
        Loaded += (_, _) => RebuildAndStart();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
    }

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public double FrameWidth
    {
        get => (double)GetValue(FrameWidthProperty);
        set => SetValue(FrameWidthProperty, value);
    }

    public double FrameHeight
    {
        get => (double)GetValue(FrameHeightProperty);
        set => SetValue(FrameHeightProperty, value);
    }

    public int FrameCount
    {
        get => (int)GetValue(FrameCountProperty);
        set => SetValue(FrameCountProperty, value);
    }

    public double Fps
    {
        get => (double)GetValue(FpsProperty);
        set => SetValue(FpsProperty, value);
    }

    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    private static void OnRequireRebuild(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SpriteSheetAnimation)d).RebuildAndStart();

    private static void OnFpsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (SpriteSheetAnimation)d;
        var fps = (double)e.NewValue;
        if (fps <= 0) return;
        ctrl._timer.Interval = TimeSpan.FromMilliseconds(1000d / fps);
        if (ctrl.IsVisible) ctrl._timer.Start();
    }

    private void RebuildAndStart()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            var frameCount = Math.Max(1, FrameCount);
            var frameWidth = FrameWidth <= 0 ? 1 : FrameWidth;
            var frameHeight = FrameHeight <= 0 ? 1 : FrameHeight;
            var scale = Scale <= 0 ? 1 : Scale;

            // 精灵表总高度 = 单帧高度 × 帧数（竖排，自顶向下）；用 Clip 逐帧裁剪，无需重复解码
            ImgSprite.Source = ImageSource;

            ScaleSprite.ScaleX = scale;
            ScaleSprite.ScaleY = scale;
            Width = frameWidth * scale;
            Height = frameHeight * scale;

            _frameIndex = Math.Clamp(_frameIndex, 0, frameCount - 1);
            UpdateFrame(frameCount, frameWidth, frameHeight);

            if (IsVisible || IsLoaded)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(1000d / Math.Max(1, Fps));
                _timer.Start();
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void Advance()
    {
        var frameCount = Math.Max(1, FrameCount);
        _frameIndex = (_frameIndex + 1) % frameCount;
        var frameWidth = FrameWidth <= 0 ? 1 : FrameWidth;
        var frameHeight = FrameHeight <= 0 ? 1 : FrameHeight;
        UpdateFrame(frameCount, frameWidth, frameHeight);
    }

    private void UpdateFrame(int frameCount, double frameWidth, double frameHeight)
    {
        var y = Math.Clamp(_frameIndex, 0, frameCount - 1) * frameHeight;
        ClipFrame.Rect = new Rect(0, y, frameWidth, frameHeight);
    }
}