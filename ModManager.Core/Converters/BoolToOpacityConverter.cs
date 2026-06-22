using Microsoft.UI.Xaml.Data;
using System;

namespace ModManager.Core.Converters
{
    /// <summary>
    /// bool → double 透明度转换。
    /// true  → 1.0（完全不透明），false → 0.4（半透明）。
    /// 用于角色/Mod 启用/禁用状态的图片与文字透明度绑定。
    /// </summary>
    public class BoolToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            (value is bool b && b) ? 1.0 : 0.4;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotImplementedException();
    }
}
