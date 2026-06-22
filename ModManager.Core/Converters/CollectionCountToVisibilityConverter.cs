using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using System;

namespace ModManager.Core.Converters
{
	public class CollectionCountToVisibilityConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value is int count && count > 0)
				return Visibility.Visible;
			return Visibility.Collapsed;
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			throw new NotImplementedException();
		}
	}
}