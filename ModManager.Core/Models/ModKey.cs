using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModManager.Core.Models
{
	public class ModKey : INotifyPropertyChanged
	{
		private string _currentIniValue = "";

		public string KeyName { get; set; }
		public string KeyType { get; set; }
		public string KeyValue { get; set; }
		public string VariableName { get; set; }
		public string InitialValue { get; set; }

		public string CurrentIniValue
		{
			get => _currentIniValue;
			set { if (_currentIniValue != value) { _currentIniValue = value; OnPropertyChanged(); } }
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void OnPropertyChanged([CallerMemberName] string? name = null)
			=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
