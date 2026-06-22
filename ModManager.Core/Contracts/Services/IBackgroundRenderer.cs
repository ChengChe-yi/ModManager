using System.Threading.Tasks;
using ModManager.Core.Models;

namespace ModManager.Core.Contracts.Services
{
	public interface IBackgroundRenderer
	{
		Task<BackgroundRenderResult?> GetBackgroundAsync(ServerType server, bool preferVideo);
		Task<BackgroundRenderResult?> GetCustomBackgroundAsync(string path);
	}
}