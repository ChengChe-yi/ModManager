using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace ModManager.Animations
{
    /// <summary>
    /// 全局隐式颜色动画注册 — 动画 3。
    /// 在页面/窗口初始化时调用一次，之后任何 SolidColorBrush / GradientBrush
    /// 的颜色变化都自动 500ms 动画过场。
    ///
    /// 等价 Compose: animateColorAsState(color, tween(500))
    /// </summary>
    public static class ImplicitColorAnimation
    {
        /// <summary>
        /// 为元素及其所有子元素的 Composition Visual 注册隐式颜色动画。
        /// 之后任何 Brush 颜色属性变化都自动 500ms 过渡。
        /// </summary>
        public static void RegisterForElement(FrameworkElement rootElement)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(rootElement).Compositor;
            var implicitAnimations = compositor.CreateImplicitAnimationCollection();

            // 对颜色属性的任何变化，自动用 500ms 动画过渡
            var colorAnim = compositor.CreateColorKeyFrameAnimation();
            colorAnim.InsertExpressionKeyFrame(0f, "this.StartingValue");
            colorAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
            colorAnim.Duration = TimeSpan.FromMilliseconds(500); // tween(500)
            colorAnim.Target = "Color";

            implicitAnimations["Color"] = colorAnim;

            // 也对渐变停止点的颜色做隐式动画
            var gradientStopAnim = compositor.CreateColorKeyFrameAnimation();
            gradientStopAnim.InsertExpressionKeyFrame(0f, "this.StartingValue");
            gradientStopAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
            gradientStopAnim.Duration = TimeSpan.FromMilliseconds(500);
            gradientStopAnim.Target = "Color";

            implicitAnimations["ColorStops"] = gradientStopAnim;

            // 应用到根元素的所有 Composition Visual
            var rootVisual = ElementCompositionPreview.GetElementVisual(rootElement);
            ApplyRecursive(rootVisual, implicitAnimations, compositor);
        }

        private static void ApplyRecursive(
            Visual visual,
            ImplicitAnimationCollection animations,
            Compositor compositor)
        {
            if (visual == null) return;

            visual.ImplicitAnimations = animations;

            if (visual is ContainerVisual container)
            {
                foreach (var child in container.Children)
                {
                    ApplyRecursive(child, animations, compositor);
                }
            }
        }
    }
}
