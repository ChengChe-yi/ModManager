using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace ModManager.Animations
{
    public sealed class ProgressBarController : IDisposable
    {
        private readonly Compositor _compositor;
        private readonly ContainerVisual _rootContainer;
        private readonly SpriteVisual _fillVisual;
        private readonly CompositionLinearGradientBrush _fillBrush;
        private readonly FrameworkElement _hostElement;

        private bool _isIndeterminate;
        private float _currentPercent;
        private CompositionPropertySet? _indeterminateParams;
        private bool _disposed;

        private const int IndeterminateCycleMs = 2000;
        private const int DeterminateDurationMs = 250;

        public ProgressBarController(FrameworkElement progressBarContainer)
        {
            _hostElement = progressBarContainer;
            _compositor = ElementCompositionPreview.GetElementVisual(progressBarContainer).Compositor;
            _rootContainer = _compositor.CreateContainerVisual();

            _fillVisual = _compositor.CreateSpriteVisual();
            _fillVisual.Size = new System.Numerics.Vector2(0, 5);
            _fillVisual.AnchorPoint = new System.Numerics.Vector2(0, 0f);

            _fillBrush = _compositor.CreateLinearGradientBrush();
            _fillBrush.StartPoint = new System.Numerics.Vector2(0, 0);
            _fillBrush.EndPoint = new System.Numerics.Vector2(1, 0);
            _fillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.0f,
                Color.FromArgb(255, 71, 145, 191)));
            _fillBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1.0f,
                Color.FromArgb(255, 184, 93, 255)));
            _fillVisual.Brush = _fillBrush;

            _rootContainer.Children.InsertAtTop(_fillVisual);
            ElementCompositionPreview.SetElementChildVisual(progressBarContainer, _rootContainer);
        }

        public void UpdateProgress(double percent, Color startColor, Color endColor)
        {
            if (_disposed) return;
            if (_isIndeterminate)
            {
                StopIndeterminate();
                _isIndeterminate = false;
            }

            var clamped = (float)Math.Clamp(percent, 0.0, 1.0);
            _currentPercent = clamped;
            var containerWidth = GetContainerWidth();
            var targetSize = clamped * containerWidth;

            UpdateBrushColors(startColor, endColor);

            var widthAnim = _compositor.CreateScalarKeyFrameAnimation();
            widthAnim.InsertKeyFrame(0f, _fillVisual.Size.X);
            widthAnim.InsertKeyFrame(1f, targetSize, _compositor.CreateLinearEasingFunction());
            widthAnim.Duration = TimeSpan.FromMilliseconds(DeterminateDurationMs);
            widthAnim.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

            _fillVisual.IsVisible = true;
            _fillVisual.StartAnimation("Size.X", widthAnim);
        }

        public void StartIndeterminate(Color startColor, Color endColor)
        {
            if (_disposed || _isIndeterminate) return;

            _fillVisual.StopAnimation("Size.X");
            _fillVisual.StopAnimation("Offset.X");
            _isIndeterminate = true;

            var containerWidth = GetContainerWidth();

            _indeterminateParams = _compositor.CreatePropertySet();
            _indeterminateParams.InsertScalar("Position", 0f);
            _indeterminateParams.InsertScalar("ContainerWidth", containerWidth);

            UpdateBrushColors(startColor, endColor);
            _fillVisual.Size = new System.Numerics.Vector2(6, 5);
            _fillVisual.IsVisible = true;

            var offsetExpr = _compositor.CreateExpressionAnimation(
                "params.Position * (params.ContainerWidth - bar.Size.X)");
            offsetExpr.SetReferenceParameter("params", _indeterminateParams);
            offsetExpr.SetReferenceParameter("bar", _fillVisual);
            _fillVisual.StartAnimation("Offset.X", offsetExpr);

            var maxWidth = containerWidth * 0.75f;
            var widthAnim = _compositor.CreateScalarKeyFrameAnimation();
            widthAnim.InsertKeyFrame(0.00f, 6f);
            widthAnim.InsertKeyFrame(0.25f, maxWidth);
            widthAnim.InsertKeyFrame(0.50f, 6f);
            widthAnim.InsertKeyFrame(0.75f, maxWidth * 0.4f);
            widthAnim.InsertKeyFrame(1.00f, 6f);
            widthAnim.Duration = TimeSpan.FromMilliseconds(IndeterminateCycleMs);
            widthAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            _fillVisual.StartAnimation("Size.X", widthAnim);

            var posAnim = _compositor.CreateScalarKeyFrameAnimation();
            posAnim.InsertKeyFrame(0f, 0f);
            posAnim.InsertKeyFrame(1f, 1f, _compositor.CreateLinearEasingFunction());
            posAnim.Duration = TimeSpan.FromMilliseconds(IndeterminateCycleMs);
            posAnim.IterationBehavior = AnimationIterationBehavior.Forever;
            _indeterminateParams.StartAnimation("Position", posAnim);
        }

        public void StopIndeterminate()
        {
            _isIndeterminate = false;
            _fillVisual.StopAnimation("Size.X");
            _fillVisual.StopAnimation("Offset.X");
            _indeterminateParams?.StopAnimation("Position");
            _indeterminateParams = null;
        }

        public void SetImmediate(double percent, Color startColor, Color endColor)
        {
            if (_disposed) return;
            StopIndeterminate();
            _isIndeterminate = false;

            var clamped = (float)Math.Clamp(percent, 0.0, 1.0);
            _currentPercent = clamped;
            var containerWidth = GetContainerWidth();

            UpdateBrushColors(startColor, endColor);
            _fillVisual.Size = new System.Numerics.Vector2(clamped * containerWidth, 5);
            _fillVisual.IsVisible = true;
        }

        public void Hide()
        {
            if (_disposed) return;
            StopIndeterminate();
            _fillVisual.StopAnimation("Size.X");
            _fillVisual.IsVisible = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopIndeterminate();
            _fillVisual.StopAnimation("Size.X");
            _fillVisual.StopAnimation("Offset.X");
            _rootContainer.Children.RemoveAll();
            // CompositionObject 由 Compositor 管理生命周期，不显式 Dispose
        }

        private float GetContainerWidth()
        {
            var w = (float)_hostElement.ActualWidth;
            return w > 0 ? w : 600f;
        }

        private void UpdateBrushColors(Color start, Color end)
        {
            if (_fillBrush.ColorStops.Count >= 2)
            {
                _fillBrush.ColorStops[0].Color = start;
                _fillBrush.ColorStops[1].Color = end;
            }
        }
    }
}
