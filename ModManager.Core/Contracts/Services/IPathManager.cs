using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace ModManager.Core.Contracts.Services
{
	public interface IPathManager
	{
		string ModsFolderPath { get; }
		Task InitializeAsync();
		Task<bool> SetModsFolderPathAsync(string newPath);
		Task ResetToDefaultPathAsync();
		Task UpdateModsFolderPathAsync(string newPath);

		event EventHandler<string> PathChanged;
	}
}
