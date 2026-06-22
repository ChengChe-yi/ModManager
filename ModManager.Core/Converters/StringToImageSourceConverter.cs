using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ModManager.Core.Converters
{
	public class StringToImageSourceConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value is string path && !string.IsNullOrEmpty(path))
			{
				try
				{
					// 使用 file:/// 协议加载本地图片
					string uriString = "file:///" + path.Replace('\\', '/');
					return new BitmapImage(new Uri(uriString))
					{
						DecodePixelType = DecodePixelType.Logical,
						DecodePixelWidth = 300   // 控制提示图大小
					};
				}
				catch { /* 文件异常则返回 null */ }
			}
			return null;
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
			=> throw new NotImplementedException();
	}
}