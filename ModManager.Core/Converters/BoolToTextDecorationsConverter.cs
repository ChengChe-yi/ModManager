using Microsoft.UI.Xaml.Data;
using System;
using Windows.UI.Text;

namespace ModManager.Core.Converters
{
    /// <summary>
    /// bool → TextDecorations 转换。
    /// true（启用）→ None，false（禁用）→ Strikethrough（中间横线）。
    /// 用于角色/Mod 禁用时角色名加删除线。
    /// </summary>
    public class BoolToTextDecorationsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            (value is bool b && b) ? TextDecorations.None : TextDecorations.Strikethrough;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotImplementedException();
    }
}
