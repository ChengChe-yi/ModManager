using System;
using System.Collections.Generic;
using System.Text;

namespace ModManager.Core.Contracts.Services
{
	public interface IModCategoryService
	{
		bool IsFolderDisabled(string folderPath);
		bool IsNameDisabled(string name);
		string GetToggledName(string currentName);
		string GetDisplayName(string folderName);

	}
}
