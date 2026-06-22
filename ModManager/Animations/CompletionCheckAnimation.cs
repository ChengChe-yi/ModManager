using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace ModManager.Animations
{
    /// <summary>
    /// 完成标记 ✓ 弹入动画 — 动画 7。
    /// 当 progress==100% 时，✓ 图标以弹性缩放效果弹出。
    ///
    /// 等价 Compose: AnimatedVisibility + spring 缩放效果。
    /// </summary>
    public static class CompletionCheckAnimation
    {
        /// <summary>
        /// 播放完成检查图标的弹入动画。
        /// 缩放从 0.5→1.0 (spring)，透明度从 0→1。
        /// </summary>
        public static void Play(FrameworkElement checkIconElement)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(checkIconElement).Compositor;
            var visual = ElementCompositionPreview.GetElementVisual(checkIconElement);

            // 确保 Visual 可见
            visual.IsVisible = true;
            visual.Opacity = 0f;

            // 缩放弹入（PopIn 效果，带弹性）
            var scaleAnim = compositor.CreateSpringVector3Animation();
            scaleAnim.InitialValue = new System.Numerics.Vector3(0.5f, 0.5f, 1f);
            scaleAnim.FinalValue = new System.Numerics.Vector3(1f, 1f, 1f);
            scaleAnim.DampingRatio = 0.6f;    // 有明显弹性
            scaleAnim.Period = TimeSpan.FromMilliseconds(50);

            // 透明度淡入
            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0f, 0f);
            opacityAnim.InsertKeyFrame(1f, 1f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(200);

            // 旋转微动画（从-10度旋转到0）
            var rotationAnim = compositor.CreateScalarKeyFrameAnimation();
            rotationAnim.InsertKeyFrame(0f, -10f);
            rotationAnim.InsertKeyFrame(1f, 0f);
            rotationAnim.Duration = TimeSpan.FromMilliseconds(300);

            visual.StartAnimation("Scale.X", scaleAnim);
            visual.StartAnimation("Scale.Y", scaleAnim);
            visual.StartAnimation("Opacity", opacityAnim);
            visual.StartAnimation("RotationAngleInDegrees", rotationAnim);
        }
    }
}
