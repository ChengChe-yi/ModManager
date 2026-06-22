using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ModManager.Helpers
{
	public class EnumToBooleanConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value == null || parameter == null) return false;
			return value.Equals(parameter);
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			return (value is true) ? parameter : DependencyProperty.UnsetValue;
		}
	}

	public class EnumToIntConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			return System.Convert.ToInt32(value);
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			return value;
		}
	}

	public class BoolToVisibilityConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			return (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			throw new NotImplementedException();
		}
	}

	public class StringFormatConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value == null || parameter == null) return value?.ToString() ?? "";
			return string.Format(parameter.ToString(), value);
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			throw new NotImplementedException();
		}
	}
}
