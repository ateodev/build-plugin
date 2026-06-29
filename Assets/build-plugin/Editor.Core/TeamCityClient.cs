using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Ateo.Build
{
	/// <summary>
	/// Thin async client over the TeamCity REST API - the plugin's control-plane link to the build server.
	/// Triggers builds (the <c>unitybuild.*</c> contract), reads status/history filtered per game/definition,
	/// and lists/downloads artifacts. Auth is the user's own permission-scoped access token (Bearer), never
	/// an admin token and never committed. Plain HTTP/JSON; no server-side coupling beyond the REST surface.
	/// </summary>
	public sealed class TeamCityClient : IDisposable
	{
		#region Fields

		private readonly string _baseUrl;
		private readonly HttpClient _http;

		#endregion

		#region Constructor

		public TeamCityClient(string baseUrl, string token)
		{
			_baseUrl = (baseUrl ?? "").TrimEnd('/');
			_http = new HttpClient();
			_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
			_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		}

		#endregion

		#region Public Methods

		/// <summary>Queue a build of <paramref name="buildTypeId"/> with the given build parameters. Returns the queued build id.</summary>
		public async Task<long> TriggerBuildAsync(string buildTypeId, IReadOnlyDictionary<string, string> properties)
		{
			TriggerRequest request = new TriggerRequest
			{
				buildType = new BuildTypeRef { id = buildTypeId },
				properties = ToProperties(properties)
			};

			string body = JsonUtility.ToJson(request);
			using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json"))
			using (HttpResponseMessage response = await _http.PostAsync(_baseUrl + "/app/rest/buildQueue", content))
			{
				string json = await response.Content.ReadAsStringAsync();
				if (!response.IsSuccessStatusCode) throw new Exception("TriggerBuild failed (" + (int)response.StatusCode + "): " + Trim(json));

				BuildDto queued = JsonUtility.FromJson<BuildDto>(json);
				return queued.id;
			}
		}

		/// <summary>Current state/status of one build.</summary>
		public async Task<BuildStatus> GetBuildAsync(long id)
		{
			string json = await GetAsync("/app/rest/builds/id:" + id + "?fields=id,number,state,status,statusText,percentageComplete,webUrl");
			return ToStatus(JsonUtility.FromJson<BuildDto>(json));
		}

		/// <summary>Recent builds of <paramref name="buildTypeId"/>, optionally filtered to one game token.</summary>
		public async Task<List<BuildStatus>> ListBuildsAsync(string buildTypeId, string gameToken, int count = 20)
		{
			string locator = "buildType:(id:" + buildTypeId + ")";
			if (!string.IsNullOrEmpty(gameToken)) locator += ",property:(name:unitybuild.game,value:" + gameToken + ")";
			locator += ",count:" + count;

			string json = await GetAsync("/app/rest/builds?locator=" + Uri.EscapeDataString(locator) +
				"&fields=build(id,number,state,status,statusText,percentageComplete,webUrl)");
			BuildListDto list = JsonUtility.FromJson<BuildListDto>(json);

			List<BuildStatus> result = new List<BuildStatus>();
			if (list != null && list.build != null)
			{
				foreach (BuildDto dto in list.build) result.Add(ToStatus(dto));
			}

			return result;
		}

		/// <summary>
		/// The team's in-flight builds (Activity view, §12.4): the queue (<c>/buildQueue</c>, with position) merged
		/// with everything currently running (<c>state:running</c>). Token-scoped by the server, so cross-team builds
		/// are invisible. Each row carries game/definition (from the recorded <c>unitybuild.*</c> properties), the
		/// owning executor, status, progress and - while running - the agent. Queued first (by position), then running.
		/// </summary>
		public async Task<List<BuildStatus>> ListInFlightAsync()
		{
			List<BuildStatus> result = new List<BuildStatus>();

			string queueJson = await GetAsync("/app/rest/buildQueue?fields=" + Uri.EscapeDataString(
				"build(id,number,state,statusText,buildTypeId,properties(property(name,value)))"));
			BuildListDto queue = JsonUtility.FromJson<BuildListDto>(queueJson);
			if (queue != null && queue.build != null)
			{
				int position = 1;
				foreach (BuildDto dto in queue.build)
				{
					BuildStatus status = ToStatus(dto);
					status.QueuePosition = position++;
					result.Add(status);
				}
			}

			string runningLocator = "state:running,defaultFilter:false,count:50";
			string runningJson = await GetAsync("/app/rest/builds?locator=" + Uri.EscapeDataString(runningLocator) +
				"&fields=" + Uri.EscapeDataString(
					"build(id,number,state,status,statusText,percentageComplete,webUrl,buildTypeId," +
					"agent(name),properties(property(name,value)))"));
			BuildListDto running = JsonUtility.FromJson<BuildListDto>(runningJson);
			if (running != null && running.build != null)
			{
				foreach (BuildDto dto in running.build) result.Add(ToStatus(dto));
			}

			return result;
		}

		/// <summary>
		/// Cancel an in-flight build (§12.4). A queued build is removed from the queue
		/// (<c>DELETE /buildQueue/id:N</c>); a running build is stopped via a build-cancel request
		/// (<c>POST /builds/id:N</c>, not re-added to the queue).
		/// </summary>
		public async Task CancelAsync(long id, bool queued)
		{
			if (queued)
			{
				using (HttpResponseMessage response = await _http.DeleteAsync(_baseUrl + "/app/rest/buildQueue/id:" + id))
				{
					if (!response.IsSuccessStatusCode)
					{
						string json = await response.Content.ReadAsStringAsync();
						throw new Exception("Cancel (queued) failed (" + (int)response.StatusCode + "): " + Trim(json));
					}
				}

				return;
			}

			const string body = "{\"comment\":\"Cancelled from Build Panel\",\"readdIntoQueue\":false}";
			using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json"))
			using (HttpResponseMessage response = await _http.PostAsync(_baseUrl + "/app/rest/builds/id:" + id, content))
			{
				if (!response.IsSuccessStatusCode)
				{
					string json = await response.Content.ReadAsStringAsync();
					throw new Exception("Cancel (running) failed (" + (int)response.StatusCode + "): " + Trim(json));
				}
			}
		}

		/// <summary>
		/// Discover the platform-token -&gt; executor buildTypeId map by reading the <c>unitybuild.platform</c>
		/// parameter every executor carries. Lets the panel pick the right config for a definition's platform
		/// without the user hand-typing ids. Only configs the token can see are returned.
		/// </summary>
		public async Task<Dictionary<string, string>> DiscoverExecutorsAsync()
		{
			string json = await GetAsync("/app/rest/buildTypes?fields=buildType(id,parameters(property(name,value)))");
			BuildTypesDto list = JsonUtility.FromJson<BuildTypesDto>(json);

			Dictionary<string, string> map = new Dictionary<string, string>();
			if (list != null && list.buildType != null)
			{
				foreach (BuildTypeDto buildType in list.buildType)
				{
					if (buildType.parameters == null || buildType.parameters.property == null) continue;

					foreach (PropertyDto property in buildType.parameters.property)
					{
						if (property.name == "unitybuild.platform" && !string.IsNullOrEmpty(property.value))
						{
							map[property.value] = buildType.id;
						}
					}
				}
			}

			return map;
		}

		/// <summary>
		/// Top-level artifact <b>files</b> of a build. Directory entries (e.g. <c>logs</c>) are skipped: the children
		/// listing returns folders too, but they carry a <c>children</c> href instead of a <c>content</c> href and
		/// 404 when fetched as content - so the download path only ever sees real, downloadable files.
		/// </summary>
		public async Task<List<ArtifactFile>> ListArtifactsAsync(long id)
		{
			string json = await GetAsync("/app/rest/builds/id:" + id + "/artifacts/children?fields=file(name,size,content(href))");
			ArtifactListDto list = JsonUtility.FromJson<ArtifactListDto>(json);

			List<ArtifactFile> result = new List<ArtifactFile>();
			if (list != null && list.file != null)
			{
				foreach (ArtifactFileDto dto in list.file)
				{
					if (dto.content == null || string.IsNullOrEmpty(dto.content.href)) continue; // directory, not a file
					result.Add(new ArtifactFile(dto.name, dto.size));
				}
			}

			return result;
		}

		/// <summary>Download one artifact to a local file.</summary>
		public async Task DownloadArtifactAsync(long id, string artifactPath, string destinationFile)
		{
			string url = _baseUrl + "/app/rest/builds/id:" + id + "/artifacts/content/" + artifactPath;
			byte[] bytes = await _http.GetByteArrayAsync(url);
			Directory.CreateDirectory(Path.GetDirectoryName(destinationFile));
			File.WriteAllBytes(destinationFile, bytes);
		}

		public void Dispose()
		{
			_http.Dispose();
		}

		#endregion

		#region Private Methods

		private async Task<string> GetAsync(string path)
		{
			using (HttpResponseMessage response = await _http.GetAsync(_baseUrl + path))
			{
				string json = await response.Content.ReadAsStringAsync();
				if (!response.IsSuccessStatusCode) throw new Exception("GET " + path + " failed (" + (int)response.StatusCode + "): " + Trim(json));

				return json;
			}
		}

		private static BuildStatus ToStatus(BuildDto dto)
		{
			BuildStatus status = new BuildStatus
			{
				Id = dto.id,
				Number = dto.number,
				State = dto.state,
				Status = dto.status,
				StatusText = dto.statusText,
				PercentageComplete = dto.percentageComplete,
				WebUrl = dto.webUrl,
				BuildTypeId = dto.buildTypeId,
				Agent = dto.agent != null ? dto.agent.name : null,
				Game = FindProperty(dto, "unitybuild.game"),
				Definition = FindProperty(dto, "unitybuild.definition")
			};

			return status;
		}

		private static string FindProperty(BuildDto dto, string name)
		{
			if (dto == null || dto.properties == null || dto.properties.property == null) return null;

			foreach (PropertyDto property in dto.properties.property)
			{
				if (property != null && property.name == name) return property.value;
			}

			return null;
		}

		private static PropertiesDto ToProperties(IReadOnlyDictionary<string, string> properties)
		{
			List<PropertyDto> list = new List<PropertyDto>();
			if (properties != null)
			{
				foreach (KeyValuePair<string, string> pair in properties)
				{
					list.Add(new PropertyDto { name = pair.Key, value = pair.Value });
				}
			}

			return new PropertiesDto { property = list.ToArray() };
		}

		private static string Trim(string value)
		{
			if (string.IsNullOrEmpty(value)) return value;
			return value.Length > 300 ? value.Substring(0, 300) : value;
		}

		#endregion

		#region DTOs

		[Serializable] private sealed class TriggerRequest { public BuildTypeRef buildType; public PropertiesDto properties; }
		[Serializable] private sealed class BuildTypeRef { public string id; }
		[Serializable] private sealed class PropertiesDto { public PropertyDto[] property; }
		[Serializable] private sealed class PropertyDto { public string name; public string value; }
		[Serializable] private sealed class BuildDto { public long id; public string number; public string state; public string status; public string statusText; public int percentageComplete; public string webUrl; public string buildTypeId; public AgentDto agent; public PropertiesDto properties; }
		[Serializable] private sealed class AgentDto { public string name; }
		[Serializable] private sealed class BuildListDto { public BuildDto[] build; }
		[Serializable] private sealed class ArtifactFileDto { public string name; public long size; public HrefDto content; }
		[Serializable] private sealed class HrefDto { public string href; }
		[Serializable] private sealed class ArtifactListDto { public ArtifactFileDto[] file; }
		[Serializable] private sealed class BuildTypeDto { public string id; public ParamsDto parameters; }
		[Serializable] private sealed class ParamsDto { public PropertyDto[] property; }
		[Serializable] private sealed class BuildTypesDto { public BuildTypeDto[] buildType; }

		#endregion
	}
}
