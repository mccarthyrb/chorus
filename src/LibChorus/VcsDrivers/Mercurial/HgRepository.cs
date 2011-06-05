using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Chorus.Utilities;
using Chorus.Utilities.code;
using Nini.Ini;
using Palaso.IO;
using Palaso.Network;
using Palaso.Progress.LogBox;

namespace Chorus.VcsDrivers.Mercurial
{

	public class HgRepository : IRetrieveFileVersionsFromRepository
	{
		protected readonly string _pathToRepository;
		protected string _userName;
		protected IProgress _progress;
		private const int SecondsBeforeTimeoutOnLocalOperation = 15 * 60;
		private const int SecondsBeforeTimeoutOnMergeOperation = 15 * 60;
		private const int SecondsBeforeTimeoutOnRemoteOperation = 40 * 60;
		private bool _haveLookedIntoProxySituation;
		private string _proxyCongfigParameterString = string.Empty;


		public static string GetEnvironmentReadinessMessage(string messageLanguageId)
		{
			try
			{
				HgRunner.Run("hg version", Environment.CurrentDirectory, 5, new NullProgress());
			}
			catch (Exception)
			{
				return "Chorus requires the Mercurial version control system.  It must be installed and part of the PATH environment variable.";
			}
			return null;
		}


		/// <exception cref="Exception">This will throw when the hgrc is locked</exception>
		public RepositoryAddress GetDefaultNetworkAddress<T>()
		{
			//the first one found in the default list
			try
			{
				var paths = GetRepositoryPathsInHgrc();
				var networkPaths = paths.Where(p => p is T);

				//none found in the hgrc
				if (networkPaths.Count() == 0) //nb: because of lazy eval, the hgrc lock exception can happen here
					return null;


				var defaultAliases = GetDefaultSyncAliases();

				foreach (var path in networkPaths)
				{
					RepositoryAddress path1 = path;//avoid "acces to modified closure"
					if (defaultAliases.Any(a => a == path1.Name))
						return path;
				}
				return networkPaths.First();
			}
			catch (Exception error) //this would happen if the hgrc was locked
			{
				throw;
			}
		}


		/// <summary>
		/// Given a file path or directory path, first try to find an existing repository at this
		/// location or in one of its parents.  If not found, create one at this location.
		/// </summary>
		/// <returns></returns>
		public static HgRepository CreateOrLocate(string startingPointForPathSearch, IProgress progress)
		{
			if (!Directory.Exists(startingPointForPathSearch) && !File.Exists(startingPointForPathSearch))
			{
				throw new ArgumentException("File or directory wasn't found", startingPointForPathSearch);
			}
			if (!Directory.Exists(startingPointForPathSearch)) // if it's a file... we need a directory
			{
				startingPointForPathSearch = Path.GetDirectoryName(startingPointForPathSearch);
			}

			string root = GetRepositoryRoot(startingPointForPathSearch, ExecuteErrorsOk("root", startingPointForPathSearch, 100, progress));
			if (!string.IsNullOrEmpty(root))
			{
				return new HgRepository(root, progress);
			}
			else
			{
				/*
				 I'm leaning away from this intervention at the moment.
					string newRepositoryPath = AskUserForNewRepositoryPath(startingPath);

				 Let's see how far we can get by just silently creating it, and leave it to the future
				 or user documentation/training to know to set up a repository at the level they want.
				*/
				string newRepositoryPath = startingPointForPathSearch;

				if (!string.IsNullOrEmpty(startingPointForPathSearch) && Directory.Exists(newRepositoryPath))
				{
					CreateRepositoryInExistingDir(newRepositoryPath, progress);

					//review: Machine name would be more accurate, but most people have, like "Compaq" as their machine name
					//but in any case, this is just a default until they set the name explicity
					var hg = new HgRepository(newRepositoryPath, progress);
					hg.SetUserNameInIni(System.Environment.UserName, progress);
					return new HgRepository(newRepositoryPath, progress);
				}
				else
				{
					return null;
				}
			}
		}

		//        protected Revision GetMyHead()
		//        {
		//            using (new ConsoleProgress("Getting real head of {0}", _userName))
		//            {
		////                string result = GetTextFromQuery(_pathToRepository, "identify -nib");
		////                string[] parts = result.Split(new char[] {' ','(',')'}, StringSplitOptions.RemoveEmptyEntries);
		////                Revision descriptor = new Revision(this, parts[2],parts[1], parts[0], "unknown");
		//
		//
		//                return descriptor;
		//            }
		//        }


		public HgRepository(string pathToRepository, IProgress progress)
		{
			Guard.AgainstNull(progress, "progress");
			_pathToRepository = pathToRepository;

			// make sure it exists
			if (GetIsLocalUri(_pathToRepository) && !Directory.Exists(_pathToRepository))
				Directory.CreateDirectory(_pathToRepository);

			_progress = progress;

			_userName = GetUserIdInUse();
		}

		public bool GetFileIsInRepositoryFromFullPath(string fullPath)
		{
			if (fullPath.IndexOf(_pathToRepository) < 0)
			{
				throw new ArgumentException(
					string.Format("GetFileIsInRepositoryFromFullPath() requies the argument {0} be a child of the root {1}",
					fullPath,
					_pathToRepository));

			}
			string subPath = fullPath.Replace(_pathToRepository, "");
			if (subPath.StartsWith(Path.DirectorySeparatorChar.ToString()))
			{
				subPath = subPath.Remove(0, 1);
			}
			return GetFileExistsInRepo(subPath);
		}

		protected void SetupPerson(string pathToRepository, string userName)
		{
			_progress.WriteVerbose("setting name and branch");
			using (new ShortTermEnvironmentalVariable("HGUSER", userName))
			{
				Execute(SecondsBeforeTimeoutOnLocalOperation, "branch", userName);
			}
		}

		/// <returns>true if changes were received</returns>
		public bool TryToPull(string repositoryLabel, string resolvedUri)
		{
			HgRepository repo = new HgRepository(resolvedUri, _progress);

			//repo.UserName = repositoryLabel;
			return PullFromRepository(repo, false);
		}

		public void Push(RepositoryAddress address, string targetUri, IProgress progress)
		{
			_progress.WriteStatus("Sending changes to {0}", address.GetFullName(targetUri));
			_progress.WriteVerbose("({0} is {1})", address.GetFullName(targetUri), targetUri);
			try
			{
				Execute(SecondsBeforeTimeoutOnRemoteOperation, "push --debug "+GetProxyConfigParameterString(targetUri,progress), SurroundWithQuotes(targetUri));
			}
			catch (Exception err)
			{
				_progress.WriteWarning("Could not send to " + targetUri + Environment.NewLine + err.Message);
			}

			if (GetIsLocalUri(targetUri))
			{
				try
				{
					Execute(SecondsBeforeTimeoutOnLocalOperation, "update", "-C"); // for usb keys and other local repositories
				}
				catch (Exception err)
				{
					_progress.WriteWarning("Could not update the actual files after a pushing to " + targetUri +
										   Environment.NewLine + err.Message);
				}
			}
		}

		private bool GetIsLocalUri(string uri)
		{
			return !(uri.StartsWith("http") || uri.StartsWith("ssh"));
		}

		/// <summary>
		/// Pull from the given repository
		/// </summary>
		/// <exception cref="unknown">Throws if could not connect</exception>
		/// <returns>true if the pull happend and changes were pulled in</returns>
		protected bool PullFromRepository(HgRepository otherRepo, bool throwIfCannot)
		{

			_progress.WriteStatus("Receiving any changes from {0}", otherRepo.Name);
			_progress.WriteVerbose("({0} is {1})", otherRepo.Name, otherRepo._pathToRepository);
			{
				try
				{
					var tip = GetTip();
					Execute(SecondsBeforeTimeoutOnRemoteOperation, "pull --debug"+GetProxyConfigParameterString(otherRepo.PathToRepo,_progress), otherRepo.PathWithQuotes);

					var newTip = GetTip();
					if (tip == null)
						return newTip != null;
					return tip.Number.Hash != newTip.Number.Hash; //review... I believe you can't pull without getting a new tip
				}
				catch (Exception err)
				{
					if (throwIfCannot)
					{
						throw err;
					}
				   _progress.WriteWarning("Could not receive from " + otherRepo.Name);
				   if(err.Message.Contains("authorization"))
					{
						_progress.WriteError("The server rejected the project name, user name, or password.");
					}
					throw err;
				}
			}
		}


		private List<Revision> GetBranches()
		{
			string what = "branches";
			_progress.WriteVerbose("Getting {0} of {1}", what, _userName);
			string result = GetTextFromQuery(what);

			string[] lines = result.Split('\n');
			List<Revision> branches = new List<Revision>();
			foreach (string line in lines)
			{
				if (line.Trim() == "")
					continue;

				string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length < 2)
					continue;
				string[] revisionParts = parts[1].Split(':');
				branches.Add(new Revision(this, parts[0], revisionParts[0], revisionParts[1], "unknown"));
			}
			return branches;
		}

		public Revision GetTip()
		{
			var rev = GetRevisionsFromQuery("tip").FirstOrDefault();
			if (rev == null || rev.Number.LocalRevisionNumber == "-1")
				return null;
			return rev;
		}

		public List<Revision> GetHeads()
		{
			_progress.WriteVerbose("Getting heads of {0}", _userName);
			return GetRevisionsFromQuery("heads");
		}


		protected string GetTextFromQuery(string query)
		{
			ExecutionResult result = ExecuteErrorsOk(query + " -R " + SurroundWithQuotes(_pathToRepository), _pathToRepository, SecondsBeforeTimeoutOnLocalOperation, _progress);
			// Debug.Assert(string.IsNullOrEmpty(result.StandardError), result.StandardError);

			if (!string.IsNullOrEmpty(result.StandardOutput))
				_progress.WriteVerbose(result.StandardOutput.Trim());
			if (!string.IsNullOrEmpty(result.StandardError))
				_progress.WriteVerbose(result.StandardError.Trim());
			if (GetHasLocks())
			{
				_progress.WriteWarning("Hg Command {0} left lock", query);
			}
			return result.StandardOutput;
		}

		protected string GetTextFromQuery(string query, int secondsBeforeTimeoutOnLocalOperation)
		{
			ExecutionResult result = ExecuteErrorsOk(query, _pathToRepository, secondsBeforeTimeoutOnLocalOperation, _progress);
			//TODO: we need a way to get this kind of error back the devs for debugging
			Debug.Assert(string.IsNullOrEmpty(result.StandardError), result.StandardError);

			return result.StandardOutput;
		}

		/// <summary>
		/// Method only for testing.
		/// </summary>
		/// <param name="filePath"></param>
		internal void AddSansCommit(string filePath)
		{
			TrackFile(filePath);
		}

		public void AddAndCheckinFile(string filePath)
		{
			TrackFile(filePath);
			Commit(false, " Add " + Path.GetFileName(filePath));
		}

		private void TrackFile(string filePath)
		{
			_progress.WriteVerbose("Adding {0} to the files that are tracked for {1}: ", Path.GetFileName(filePath),
								   _userName);
			Execute(SecondsBeforeTimeoutOnLocalOperation, "add", SurroundWithQuotes(filePath));
		}

		public virtual void Commit(bool forceCreationOfChangeSet, string message, params object[] args)
		{
			message = string.Format(message, args);
			_progress.WriteVerbose("{0} committing with comment: {1}", _userName, message);
			ExecutionResult result = Execute(SecondsBeforeTimeoutOnLocalOperation, "ci", "-m " + SurroundWithQuotes(message));
			_progress.WriteVerbose(result.StandardOutput);
		}




		public void Branch(string branchName)
		{
			_progress.WriteVerbose("{0} changing working dir to branch: {1}", _userName, branchName);
			Execute(SecondsBeforeTimeoutOnLocalOperation, "branch -f ", SurroundWithQuotes(branchName));
		}

		protected ExecutionResult Execute(int secondsBeforeTimeout, string cmd, params string[] rest)
		{
			return Execute(false, secondsBeforeTimeout, cmd, rest);
		}

		/// <summary>
		///
		/// </summary>
		/// <exception cref="System.TimeoutException"/>
		/// <returns></returns>
		protected ExecutionResult Execute(bool failureIsOk, int secondsBeforeTimeout, string cmd, params string[] rest)
		{
			if(_progress.CancelRequested)
			{
				throw new UserCancelledException();
			}
			StringBuilder b = new StringBuilder();
			b.Append(cmd + " ");
			if (!string.IsNullOrEmpty(_pathToRepository))
			{
				b.Append("-R " + SurroundWithQuotes(_pathToRepository) + " ");
			}
			foreach (string s in rest)
			{
				b.Append(s + " ");
			}

			ExecutionResult result = ExecuteErrorsOk(b.ToString(), _pathToRepository, secondsBeforeTimeout, _progress);
			if (ProcessOutputReader.kCancelled == result.ExitCode)
			{
				_progress.WriteWarning("User Cancelled");
				return result;
			}
			if (0 != result.ExitCode && !failureIsOk)
			{
				var details = Environment.NewLine + "hg Command was " + Environment.NewLine + b.ToString();
				try
				{
					var versionInfo = GetTextFromQuery("version", secondsBeforeTimeout);
					//trim the verbose copyright stuff
					versionInfo = versionInfo.Substring(0, versionInfo.IndexOf("Copyright"));
					details += Environment.NewLine + "hg version is: " + versionInfo;
				}
				catch (Exception)
				{
					details += Environment.NewLine + "Could not get HG VERSION";

				}


				if (!string.IsNullOrEmpty(result.StandardError))
				{
					throw new ApplicationException(result.StandardError + details);
				}
				else
				{
				throw new ApplicationException("Got return value " + result.ExitCode + details);
			}
			}
			return result;
		}

		/// <exception cref="System.TimeoutException"/>
		protected static ExecutionResult ExecuteErrorsOk(string command, string fromDirectory, int secondsBeforeTimeout, IProgress progress)
		{
			if (progress.CancelRequested)
			{
				throw new UserCancelledException();
			}

#if DEBUG
			if (GetHasLocks(fromDirectory, progress))
			{
				progress.WriteWarning("Found a lock before exectuting: {0}.", command);
			}
#endif

			progress.WriteVerbose("Executing: " + command);
			var result = HgRunner.Run("hg " + command, fromDirectory, secondsBeforeTimeout, progress);
			if (result.DidTimeOut)
			{
				throw new TimeoutException(result.StandardError);
			}
			if (result.UserCancelled)
			{
				throw new UserCancelledException();
			}
			if (!string.IsNullOrEmpty(result.StandardError))
			{
				progress.WriteVerbose("standerr: " + result.StandardError);//not necessarily and *error*, down this deep
			}
			if (!string.IsNullOrEmpty(result.StandardOutput))
			{
				progress.WriteVerbose("standout: " + result.StandardOutput);//not necessarily and *error*, down this deep
			}

#if DEBUG
			//nb: store/lock is so common with recover (in hg 1.3) that we don't even want to mention it
			if (!command.Contains("recover") && GetHasLocks(fromDirectory, progress))
			{
				progress.WriteWarning("{0} left a lock.", command);
			}
#endif
			return result;
		}

		//        /// <exception cref="System.TimeoutException"/>
		//        protected static ExecutionResult ExecuteErrorsOk(string command, int secondsBeforeTimeout, IProgress progress)
		//        {
		//            return ExecuteErrorsOk(command, null, secondsBeforeTimeout, progress);
		//        }




		public string PathWithQuotes
		{
			get
			{
				return "\"" + _pathToRepository + "\"";
			}
		}

		public string PathToRepo
		{
			get { return _pathToRepository; }
		}

//        public string UserName
//        {
//            get { return _userName; }
//            set { _userName = value; }
//        }

		private string Name
		{
			get { return _userName; } //enhance... location is important, too
		}

		public string GetFilePath(string name)
		{
			return Path.Combine(_pathToRepository, name);
		}

		public List<string> GetChangedFiles()
		{
			ExecutionResult result = Execute(SecondsBeforeTimeoutOnLocalOperation, "status");
			string[] lines = result.StandardOutput.Split('\n');
			List<string> files = new List<string>();
			foreach (string line in lines)
			{
				if (line.Trim() != "")
					files.Add(line.Substring(2)); //! data.txt
			}

			return files;
		}

		public void Update()
		{
			_progress.WriteVerbose("{0} updating", _userName);
			Execute(SecondsBeforeTimeoutOnLocalOperation, "update", "-C");
		}

		public void Update(string revision)
		{
			_progress.WriteVerbose("{0} updating (making working directory contain) revision {1}", _userName, revision);
			Execute(SecondsBeforeTimeoutOnLocalOperation, "update", "-r", revision, "-C");
		}

		//        public void GetRevisionOfFile(string fileRelativePath, string revision, string fullOutputPath)
		//        {
		//            //for "hg cat" (surprisingly), the relative path isn't relative to the start of the repo, but to the current
		//            // directory.
		//            string absolutePathToFile = SurroundWithQuotes(Path.Combine(_pathToRepository, fileRelativePath));
		//
		//            Execute("cat", _pathToRepository, "-o ",fullOutputPath," -r ",revision,absolutePathToFile);
		//        }

		public static void CreateRepositoryInExistingDir(string path, IProgress progress)
		{
			var repo = new HgRepository(path, progress);
			repo.Execute(20, "init", SurroundWithQuotes(path));
		}


		public void AddAndCheckinFiles(List<string> includePatterns, List<string> excludePatterns, string message)
		{
			StringBuilder args = new StringBuilder();
			foreach (string pattern in includePatterns)
			{
				string p = Path.Combine(_pathToRepository, pattern);
				args.Append(" -I " + SurroundWithQuotes(p));
			}

			foreach (string pattern in excludePatterns)
			{
				//this fails:   hg add -R "E:\Users\John\AppData\Local\Temp\ChorusTest"  -X "**/cache"
				//but this works  -X "E:\Users\John\AppData\Local\Temp\ChorusTest/**/cache"
				string p = Path.Combine(_pathToRepository, pattern);
				args.Append(" -X " + SurroundWithQuotes(p));
			}

			//enhance: what happens if something is covered by the exclusion pattern that was previously added?  Will the old
			// version just be stuck on the head? NB: to remove a file from the checkin but not delete it, do "hg remove -Af"

			if (GetIsAtLeastOneMissingFileInWorkingDir())
			{
				_progress.WriteVerbose(
					"At least one file was removed from the working directory.  Telling Hg to record the deletion.");

				Execute(SecondsBeforeTimeoutOnLocalOperation, "rm -A");
			}

			_progress.WriteVerbose("Adding files to be tracked ({0}", args.ToString());
			Execute(SecondsBeforeTimeoutOnLocalOperation, "add", args.ToString());

			_progress.WriteVerbose("Committing \"{0}\"", message);
			Commit(false, message);
		}

		public static string GetRepositoryRoot(string directoryPath, ExecutionResult secondsBeforeTimeout)
		{
			//            string old = Directory.GetCurrentDirectory();
			//            try
			//            {
			// Directory.SetCurrentDirectory(directoryPath);
			ExecutionResult result = secondsBeforeTimeout;
			if (result.ExitCode == 0)
			{
				return result.StandardOutput.Trim();
			}
			return null;
			//            }
			//            finally
			//            {
			//                Directory.SetCurrentDirectory(old);
			//            }
		}


		private void PrintHeads(List<Revision> heads, Revision myHead)
		{
			_progress.WriteMessage("Current Heads:");
			foreach (Revision head in heads)
			{
				if (head.Number.LocalRevisionNumber == myHead.Number.LocalRevisionNumber)
				{
					_progress.WriteVerbose("  ME {0} {1} {2}", head.UserId, head.Number.LocalRevisionNumber, head.Summary);
				}
				else
				{
					_progress.WriteVerbose("      {0} {1} {2}", head.UserId, head.Number.LocalRevisionNumber, head.Summary);
				}
			}
		}


		/// <summary>
		/// Will throw exception in case of error.
		/// Will never time out.
		/// Will honor state of the progress.CancelRequested property
		/// </summary>
		public static void Clone(string sourceUri, string targetPath, IProgress progress)
		{
			progress.WriteStatus("Getting project...");
			try
			{
				var repo = new HgRepository(targetPath, progress);

				repo.Execute(int.MaxValue, "clone", DoWorkOfDeterminingProxyConfigParameterString(targetPath, progress), SurroundWithQuotes(sourceUri) + " " + SurroundWithQuotes(targetPath));
				progress.WriteStatus("Finished copying to this computer at {0}", targetPath);
			}
			catch (Exception error)
			{
				if (error.Message.Contains("502"))
				{
					var x = new Uri(sourceUri);
					progress.WriteMessage("Check that the name {0} is correct", x.Host);
				}
				else if (error.Message.Contains("404"))
				{
					var x = new Uri(sourceUri);
					progress.WriteMessage("Check that {0} really hosts a project labelled '{1}'", x.Host, x.PathAndQuery.Trim('/'));
				}
				throw error;
			}
		}


		public void CloneLocal(string targetPath)
		{
			Execute(SecondsBeforeTimeoutOnLocalOperation, "clone --uncompressed", PathWithQuotes + " " + SurroundWithQuotes(targetPath));
		}

		/// <summary>
		/// here we only create the .hg, no files. This is good because the people aren't tempted to modify
		/// files in that directory, where nothing will ever check the changes in.
		/// </summary>
		public void CloneToRemoteDirectoryWithoutCheckout(string targetPath)
		{
			Execute(SecondsBeforeTimeoutOnLocalOperation, "clone -U --uncompressed", PathWithQuotes + " " + SurroundWithQuotes(targetPath));
		}

		private List<Revision> GetRevisionsFromQuery(string query)
		{
			string result = GetTextFromQuery(query);
			return GetRevisionsFromQueryResultText(result);
		}


		public List<Revision> GetAllRevisions()
		{
			/*
				changeset:   0:7ee3570760cd
				tag:         tip
				user:        hattonjohn@gmail.com
				date:        Wed Jul 02 16:40:26 2008 -0600
				summary:     bob: first one
			 */

			string result = GetTextFromQuery("log");
			return GetRevisionsFromQueryResultText(result);
		}

		public List<Revision> GetRevisionsFromQueryResultText(string queryResultText)
		{
			TextReader reader = new StringReader(queryResultText);
			string line = reader.ReadLine();


			List<Revision> items = new List<Revision>();
			Revision item = null;
#if MONO
			int infiniteLoopChecker = 0;//trying to pin down WS-14981 send/receive hangs
			while(line !=null && infiniteLoopChecker<100)
#endif
			while (line != null)
			{
				int colonIndex = line.IndexOf(":");
				if (colonIndex > 0)
				{
					string label = line.Substring(0, colonIndex);
					string value = line.Substring(colonIndex + 1).Trim();
					switch (label)
					{
						default:
#if MONO
							infiniteLoopChecker++;
#endif
							break;
						case "changeset":
							item = new Revision(this);
							items.Add(item);
							item.SetRevisionAndHashFromCombinedDescriptor(value);
							break;
#if MONO
							infiniteLoopChecker=0;
#endif
						case "parent":
							item.AddParentFromCombinedNumberAndHash(value);
							break;

						case "branch":
							item.Branch = value;
							break;

						case "user":
							item.UserId = value;
							break;

						case "date":
							item.DateString = value;
							break;

						case "summary":
							item.Summary = value;
							break;

						case "tag":
							item.Tag = value;
							break;
					}
				}
				line = reader.ReadLine();
			}

#if MONO
	if(infiniteLoopChecker >99)
	{
	   _progress.WriteWarning("Had to break out of infinite loop in GetRevisionsFromQueryResultText(). See WS-14981: 'send/receive hangs'.");
	}
#endif
			return items;
		}

		/// <summary>
		/// If we checked in now, which revision would be the parent?
		/// </summary>
		public Revision GetRevisionWorkingSetIsBasedOn()
		{
			return GetRevisionsFromQuery("parents").FirstOrDefault();
		}

		[Obsolete("Use the non-static member instead... this is just here for the old partial merger")]
		public static void SetUserId(string path, string userId)
		{
			var hg = new HgRepository(path, new NullProgress());
			hg.SetUserNameInIni(userId, new NullProgress());
			//Environment.SetEnvironmentVariable("hguser", userId);
			//defunct Execute("config", path, "--local ui.username " + userId);

		}

		public string GetUserIdInUse()
		{
			if (GetIsLocalUri(_pathToRepository))
			{
				return GetUserNameFromIni(_progress, Environment.UserName.Replace(" ", ""));
				//this gave the global name, we want the name associated with this repository
				//return GetTextFromQuery(_pathToRepository, "showconfig ui.username").Trim();
			}
			else
			{
				return GetUriStrippedOfUserAccountInfo(_pathToRepository);
			}
		}

		private string GetUriStrippedOfUserAccountInfo(string repository)
		{
			//enhance: make it handle ssh's
			Regex x = new Regex("(http://)(.+@)*(.+)");
			var s = x.Replace(repository, @"$1$3");
			return s;
		}

		public bool GetFileExistsInRepo(string subPath)
		{
			string result = GetTextFromQuery("locate " + subPath);
			return !String.IsNullOrEmpty(result.Trim());
		}
		public bool GetIsAtLeastOneMissingFileInWorkingDir()
		{
			string result = GetTextFromQuery("status -d ");
			return !String.IsNullOrEmpty(result.Trim());
		}

		/// <summary>
		///  From IRetrieveFileVersionsFromRepository
		/// </summary>
		/// <returns>path to a temp file. caller is responsible for deleting the file.</returns>
		public string RetrieveHistoricalVersionOfFile(string relativePath, string revOrHash)
		{
			Guard.Against(string.IsNullOrEmpty(revOrHash), "The revision cannot be empty (note: the first revision has an empty string for its parent revision");
			var f = TempFile.WithExtension(Path.GetExtension(relativePath));

			var cmd = string.Format("cat -o \"{0}\" -r {1} \"{2}\"", f.Path, revOrHash, relativePath);
			ExecutionResult result = ExecuteErrorsOk(cmd, _pathToRepository, SecondsBeforeTimeoutOnLocalOperation, _progress);
			if (!string.IsNullOrEmpty(result.StandardError.Trim()))
			{
				throw new ApplicationException(String.Format("Could not retrieve version {0} of {1}. Mercurial said: {2}", revOrHash, relativePath, result.StandardError));
			}
			return f.Path;
		}

		public string GetCommonAncestorOfRevisions(string rev1, string rev2)
		{
			var result = GetTextFromQuery("debugancestor " + rev1 + " " + rev2);
			return new RevisionNumber(result).LocalRevisionNumber;
		}

		public IEnumerable<FileInRevision> GetFilesInRevision(Revision revision)
		{
			List<FileInRevision> files = new List<FileInRevision>();
			//nb: there can be 2 parents, and at the moment, I don't know how to figure
			//out what changed except by comparing this to each revision (seems dumb)
			var revisionRanges = GetRevisionRangesFoDiffingARevision(revision);
			foreach (var r in revisionRanges)
			{
				var query = "status --rev " + r;
				foreach (var file in GetFilesInRevisionFromQuery(revision, query))
				{
					//only add if we don't already have it, from comparing with another parent
					if (null == files.FirstOrDefault(f => f.FullPath == file.FullPath))
					{
						if (file.ActionThatHappened != FileInRevision.Action.Unknown)
							files.Add(file);
					}
				}
			}

			return files;
		}



		public List<FileInRevision> GetFilesInRevisionFromQuery(Revision revisionToAssignToResultingFIRs, string query)
		{
			var result = GetTextFromQuery(query);
			string[] lines = result.Split('\n');
			var revisions = new List<FileInRevision>();
			foreach (string line in lines)
			{
				if (line.Trim() == "")
					continue;
				var actionLetter = line[0];
				//               if(actionLetter == '?') //this means it wasn't actually committed, like maybe ignored?
				//                 continue;
				var action = ParseActionLetter(actionLetter);

				//if this is the first rev in the whole repo, then the only way to list the fils
				//is to include the "clean" ones.  Better to represent that as an Add
				if (action == FileInRevision.Action.NoChanges)
					action = FileInRevision.Action.Added;

				string revToAssign = revisionToAssignToResultingFIRs == null ? "-1" : revisionToAssignToResultingFIRs.Number.LocalRevisionNumber;
				revisions.Add(new FileInRevision(revToAssign, Path.Combine(PathToRepo, line.Substring(2)), action));
			}
			return revisions;
		}

		private IEnumerable<string> GetRevisionRangesFoDiffingARevision(Revision revision)
		{
			var parents = GetParentsOfRevision(revision.Number.LocalRevisionNumber);
			if (parents.Count() == 0)
				yield return string.Format("{0}:{0} -A", revision.Number.LocalRevisionNumber);

			foreach (var parent in parents)
			{
				yield return string.Format("{0}:{1}", parent, revision.Number.LocalRevisionNumber);
			}
		}

		public IEnumerable<string> GetParentsOfRevision(string localRevisionNumber)
		{
			return from x in GetRevisionsFromQuery("parent -r " + localRevisionNumber)
				   select x.Number.LocalRevisionNumber;
		}

		public IEnumerable<RevisionNumber> GetParentsRevisionNumbers(string localRevisionNumber)
		{
			return from x in GetRevisionsFromQuery("parent -r " + localRevisionNumber)
				   select x.Number;
		}

		private static FileInRevision.Action ParseActionLetter(char actionLetter)
		{
			switch (actionLetter)
			{
				case 'A':
					return FileInRevision.Action.Added;
				case 'M':
					return FileInRevision.Action.Modified;
				case 'R':
					return FileInRevision.Action.Deleted;
				case '!':
					return FileInRevision.Action.Deleted;
				case 'C':
					return FileInRevision.Action.NoChanges;
				default:
					return FileInRevision.Action.Unknown;
			}
		}

		public IEnumerable<RepositoryAddress> GetRepositoryPathsInHgrc()
		{
			var section = GetHgrcDoc().Sections.GetOrCreate("paths");
			//I repent            if (section.GetKeys().Count() == 0)
			//            {
			//                yield return
			//                    RepositoryAddress.Create("LanguageDepot",
			//                                             "http://hg-public.languagedepot.org/REPLACE_WITH_ETHNOLOGUE_CODE");
			//            }
			foreach (var name in section.GetKeys())
			{
				var uri = section.GetValue(name);
				yield return RepositoryAddress.Create(name, uri, false);
			}
		}


		/// <summary>
		/// TODO: sort out this vs. the UserName property
		/// </summary>
		/// <returns></returns>
		public string GetUserNameFromIni(IProgress progress, string defaultName)
		{
			try
			{
				var p = GetPathToHgrc();
				if (File.Exists(p))
				{
					var doc = GetHgrcDoc();
					var section = doc.Sections["ui"];
					if ((section != null) && section.Contains("username"))
						return section.GetValue("username");
				}

				return defaultName;
			}
			catch (Exception)
			{
				progress.WriteStatus("Couldn't determine user name, will use {0}", defaultName);
				return defaultName;
			}
		}

		internal string GetPathToHgrc()
		{
			var d = Path.Combine(_pathToRepository, ".hg");
			return Path.Combine(d, "hgrc");
		}

		private IniDocument GetHgrcDoc()
		{
			var p = GetPathToHgrc();
			if (!File.Exists(p))
			{
				string d = Path.GetDirectoryName(p);
				if (!Directory.Exists(d))
					throw new ApplicationException("There is no repository at " + d);

				File.WriteAllText(p, "");
			}
			return new Nini.Ini.IniDocument(p, IniFileType.MercurialStyle);
		}

		private IniDocument GetMercurialIni()
		{
#if MONO
			var p = "~/.hgrc";
#else
			//NB: they're talking about moving this (but to WORSE place, my documents/mercurial)
			var profile = Environment.GetEnvironmentVariable("USERPROFILE");
			if (profile == null)
			{
				throw new ApplicationException("The %USERPROFILE% environment variable on this machine is not set.");
			}
			var p = Path.Combine(profile, "mercurial.ini");
#endif
			if (!File.Exists(p))
			{
				File.WriteAllText(p, "");
			}
			return new Nini.Ini.IniDocument(p, IniFileType.MercurialStyle);
		}

		public void SetUserNameInIni(string name, IProgress progress)
		{
			try
			{
				var doc = GetHgrcDoc();
				doc.Sections.GetOrCreate("ui").Set("username", name);
				doc.SaveAndGiveMessageIfCannot();
			}
			catch (IOException e)
			{
				progress.WriteError("Could not set the user name from the hgrc ini file: " + e.Message);
				throw new TimeoutException(e.Message, e);//review... this keeps things simpler, but for a lack of precision
			}

			catch (Exception error)
			{
				progress.WriteWarning("Could not set the user name from the hgrc ini file: " + error.Message);
			}
		}

		public void SetKnownRepositoryAddresses(IEnumerable<RepositoryAddress> addresses)
		{
			var doc = GetHgrcDoc();
			doc.Sections.RemoveSection("paths");//clear it out
			var section = doc.Sections.GetOrCreate("paths");
			foreach (var address in addresses)
			{
				section.Set(address.Name, address.URI);
			}
			doc.SaveAndGiveMessageIfCannot();
		}

		public void SetTheOnlyAddressOfThisType(RepositoryAddress address)
		{
			List<RepositoryAddress> addresses = new List<RepositoryAddress>(GetRepositoryPathsInHgrc());
			RepositoryAddress match = addresses.FirstOrDefault(p=>p.GetType()  ==  address.GetType());
			if(match!=null)
			{
				addresses.Remove(match);
			}
			addresses.Add(address);
			SetKnownRepositoryAddresses(addresses);
		}

		public Revision GetRevision(string numberOrHash)
		{
			return GetRevisionsFromQuery("log --rev " + numberOrHash).FirstOrDefault();
		}

		/// <summary>
		/// this is a chorus-specific concept, that there are 0 or more repositories
		/// which we always try to sync with
		/// </summary>
		public void SetDefaultSyncRepositoryAliases(IEnumerable<string> aliases)
		{
			var doc = GetHgrcDoc();
			doc.Sections.RemoveSection("ChorusDefaultRepositories");//clear it out
			IniSection section = GetDefaultRepositoriesSection(doc);
			foreach (var alias in aliases)
			{
				section.Set(alias, string.Empty); //so we'll have "LanguageForge =", which is weird, but it's the hgrc style
			}
			doc.SaveAndGiveMessageIfCannot();

		}

		private IniSection GetDefaultRepositoriesSection(IniDocument doc)
		{
			var section = doc.Sections.GetOrCreate("ChorusDefaultRepositories");
			section.Comment = "Used by chorus to track which repositories should always be checked.  To enable a path, enter it in the [paths] section, e.g. fiz='http://fis.com/fooproject', then in this section, just add 'fiz='";
			return section;
		}
		//
		//        public List<RepositoryAddress> GetDefaultSyncAddresses()
		//        {
		//            var list = new List<RepositoryAddress>();
		//            var doc = GetHgrcDoc();
		//            var section = GetDefaultRepositoriesSection(doc);
		//            var aliases = section.GetKeys();
		//            foreach (var path in GetRepositoryPathsInHgrc())
		//            {
		//                if (aliases.Contains<string>(path.Name))
		//                {
		//                    list.Add(path);
		//                }
		//            }
		//            return list;
		//        }

		public List<string> GetDefaultSyncAliases()
		{
			var list = new List<RepositoryAddress>();
			var doc = GetHgrcDoc();
			var section = GetDefaultRepositoriesSection(doc);
			return new List<string>(section.GetKeys());
		}

		public void EnsureTheseExtensionAreEnabled(string[] extensionNames)
		{
			var doc = GetHgrcDoc();
			var section = doc.Sections.GetOrCreate("extensions");
			foreach (var name in extensionNames)
			{
				//NB: if ever we get to setting values, checking for existence won't be enough to get the right new value!
				if (!section.GetKeys().Contains(name))
				{
					_progress.WriteMessage("Adding extension to project configuration: {0}", name);
					section.Set(name, string.Empty);
				}
			}
			doc.SaveAndThrowIfCannot();
		}

		public IEnumerable<string> GetEnabledExtension()
		{
			var doc = GetHgrcDoc();
			var section = doc.Sections.GetOrCreate("extensions");
			return section.GetKeys();
		}

		public void SetIsOneDefaultSyncAddresses(RepositoryAddress address, bool doInclude)
		{
			var doc = GetHgrcDoc();
			var section = GetDefaultRepositoriesSection(doc);
			if (doInclude)
			{
				section.Set(address.Name, string.Empty);
			}
			else
			{
				section.Remove(address.Name);
			}
			doc.SaveAndGiveMessageIfCannot();
		}

		/// <summary>
		/// Warning: this use of "incomin" takes just as long as a pull, according to the hg mailing list
		/// </summary>
		/// <param name="uri"></param>
		/// <param name="progress"></param>
		/// <returns></returns>
		public bool GetCanConnectToRemote(string uri, IProgress progress)
		{

			//this may be just as slow as a pull
			//    ExecutionResult result = ExecuteErrorsOk(string.Format("incoming -l 1 {0}", SurroundWithQuotes(uri)), _pathToRepository, SecondsBeforeTimeoutOnLocalOperation, _progress);
			//so we're going to just ping

			try
			{
				//strip everything but the host name
				Uri uriObject;
				if (!Uri.TryCreate(uri, UriKind.Absolute, out uriObject))
					return false;

				if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
				{
					progress.WriteWarning("This machine does not have a live network connection.");
					return false;
				}

			   progress.WriteVerbose("Pinging {0}...", uriObject.Host);
				using (var ping = new System.Net.NetworkInformation.Ping())
				{
					var result = ping.Send(uriObject.Host, 3000);//arbitrary... what's a reasonable wait?
					if (result.Status == IPStatus.Success)
					{
						_progress.WriteVerbose("Ping took {0} milliseconds", result.RoundtripTime);
						return true;
					}
					_progress.WriteVerbose("Ping failed. Trying google.com...");

					//ok, at least in Ukarumpa, sometimes pings just are impossible.  Determine if that's what's going on by pinging google
					result = ping.Send("google.com", 3000);
					if (result.Status != IPStatus.Success)
					{
						progress.WriteVerbose("Ping to google failed, too.");
						if (System.Net.Dns.GetHostAddresses(uriObject.Host).Count() > 0)
						{
							progress.WriteVerbose(
								"Did resolve the host name, so it's worth trying to use hg to connect... some places block ping.");
							return true;
						}
						progress.WriteVerbose("Could not resolve the host name '{0}'.", uriObject.Host);
						return false;
					}

					if (System.Net.Dns.GetHostAddresses(uriObject.Host).Count() > 0)
					{
						progress.WriteStatus(
							"Chorus could ping google, and did get IP address for {0}, but could not ping it, so it could be that the server is temporarily unavailable.", uriObject.Host);
						return true;
					}

					progress.WriteError("Please check the spelling of address {0}.  Chorus could not resolve it to an IP address.", uriObject.Host);
					return false; // assume the network is ok, but the hg server is inaccessible
				}
			}
			catch (Exception)
			{
				return false;
			}
		}



		public void RecoverFromInterruptedTransactionIfNeeded()
		{
			var result = Execute(true, SecondsBeforeTimeoutOnLocalOperation, "recover");

			if (GetHasLocks())
			{   //recover very often leaves store/lock, at least in hg 1.3
				RemoveOldLocks("hg.exe", false);
				_progress.WriteVerbose("Recover may have left a lock, which was removed unless otherwise reported.");
			}

			if (result.StandardError.StartsWith("no interrupted"))
			{
				return;
			}

			if (!string.IsNullOrEmpty(result.StandardError))
			{
				_progress.WriteError(result.StandardError);
				throw new ApplicationException("Trying to recover, got: "+result.StandardError);
			}
			if (!string.IsNullOrEmpty(result.StandardOutput))
			{
				_progress.WriteWarning("Recovered: " + result.StandardOutput);
			}

		}

		/// <summary>
		///
		/// </summary>
		/// <exception cref="ApplicationException"/>
		/// <param name="localRepositoryPath"></param>
		/// <param name="revisionNumber"></param>
		/// <returns>false if nothing needed to be merged, true if the merge was done. Throws exception if there is an error.</returns>
		public bool Merge(string localRepositoryPath, string revisionNumber)
		{
			var result = Execute(true, SecondsBeforeTimeoutOnMergeOperation, "merge", "-r", revisionNumber);

			if (result.ExitCode != 0)
			{
				if (result.StandardError.Contains("nothing to merge"))
				{
					if (!string.IsNullOrEmpty(result.StandardOutput))
					{
						_progress.WriteVerbose(result.StandardOutput);
					}
					if (!string.IsNullOrEmpty(result.StandardError))
					{
						_progress.WriteVerbose(result.StandardError);
					}
					return false;
				}
				else
				{
					_progress.WriteError(result.StandardError);
					if (!string.IsNullOrEmpty(result.StandardOutput))
					{
						_progress.WriteError("Also had this in the standard output:");
						_progress.WriteError(result.StandardOutput);
					}
					throw new ApplicationException(result.StandardError);
				}
			}
			return true;
		}

		/// <summary>
		/// Attempts to remove lock and wlocks if it looks safe to do so
		/// </summary>
		/// <returns></returns>
		public bool RemoveOldLocks()
		{
			return RemoveOldLocks("hg.exe", false);
		}


		public bool GetHasLocks()
		{
			return GetHasLocks(_pathToRepository, _progress);
		}

		public static bool GetHasLocks(string path, IProgress progress)
		{
			var wlockPath = Path.Combine(Path.Combine(path, ".hg"), "wlock");
			if (File.Exists(wlockPath))
				return true;

			var lockPath = Path.Combine(path, ".hg");
			lockPath = Path.Combine(lockPath, "store");
			lockPath = Path.Combine(lockPath, "lock");

			if (File.Exists(lockPath))
				return true;

			return false;
		}

		/// <summary>
		///
		/// </summary>
		/// <param name="processNameToMatch">the process to look for, instead of "hg.exe"</param>
		/// <param name="registerWarningIfFound"></param>
		/// <returns></returns>
		public bool RemoveOldLocks(string processNameToMatch, bool registerWarningIfFound)
		{
			var wlockPath = Path.Combine(Path.Combine(_pathToRepository, ".hg"), "wlock");
			if (!RemoveOldLockFile(processNameToMatch, wlockPath, true))
				return false;

			var lockPath = Path.Combine(_pathToRepository, ".hg");
			lockPath = Path.Combine(lockPath, "store");
			lockPath = Path.Combine(lockPath, "lock");
			return RemoveOldLockFile(processNameToMatch, lockPath, registerWarningIfFound);
		}



		private bool RemoveOldLockFile(string processNameToMatch, string pathToLock, bool registerWarningIfFound)
		{
			if (File.Exists(pathToLock))
			{
				if (registerWarningIfFound)
				{
					_progress.WriteWarning("Trying to remove a lock at {0}...", pathToLock);
				}
				var hgIsRunning = System.Diagnostics.Process.GetProcessesByName(processNameToMatch).Length > 0;
				if (hgIsRunning)
				{
					_progress.WriteError("There is at last one {0} running, so {1} cannot be removed.  You may need to restart the computer.", processNameToMatch, Path.GetFileName(pathToLock));
					return false;
				}
				try
				{
					File.Delete(pathToLock);
					if (registerWarningIfFound)
					{
						_progress.WriteWarning("Lock safely removed.");
					}
				}
				catch (Exception error)
				{
					try
					{
						var dest = Path.GetTempFileName();
						File.Delete(dest);
						File.Move(pathToLock, dest);
						_progress.WriteWarning("Lock could not be deleted, but was moved to temp directory.");
					}
					catch (Exception)
					{
						_progress.WriteError(
							"The file {0} could not be removed.  You may need to restart the computer.",
							Path.GetFileName(pathToLock));
						_progress.WriteError(error.Message);
						return false;
					}
				}
			}

			return true;
		}

		public void RollbackWorkingDirectoryToLastCheckin()
		{
			Execute(false, 30, "update --clean");
		}
		public void RollbackWorkingDirectoryToRevision(string revision)
		{
			Execute(false, 30, "update --clean --rev " + revision);
		}

		/// <summary>
		/// use this, for example, if a clone fails
		/// </summary>
		public void GetDiagnosticInformationForRemoteProject(IProgress progress, string url)
		{
			progress.WriteStatus("Gathering diagnostics data (can't actually tell you anything about the remote server)...");
			progress.WriteMessage(GetTextFromQuery("version", 30));

#if !MONO
			progress.WriteMessage("Using Mercurial at: "+MercurialLocation.PathToHgExecutable);
#endif
			progress.WriteMessage("---------------------------------------------------");

			progress.WriteMessage("remote url = " + url);

			try
			{
				progress.WriteMessage("Client = " + Assembly.GetEntryAssembly().FullName);
				progress.WriteMessage("Chorus = " + Assembly.GetExecutingAssembly().FullName);
			}
			catch (Exception)
			{
				progress.WriteWarning("Could not get all assembly info.");
			}

			progress.WriteMessage("---------------------------------------------------");

			progress.WriteMessage("config:");
			progress.WriteMessage(GetTextFromQuery("showconfig", 30));
			progress.WriteMessage("---------------------------------------------------");

			progress.WriteStatus("Done.");
		}

		public void GetDiagnosticInformation(IProgress progress)
		{
			progress.WriteStatus("Gathering diagnostics data...");
			progress.WriteMessage(GetTextFromQuery("version", 30));
   #if !MONO
			progress.WriteMessage("Using Mercurial at: "+MercurialLocation.PathToHgExecutable);
#endif
			progress.WriteMessage("---------------------------------------------------");

			progress.WriteMessage("path = " + _pathToRepository);

			try
			{
				progress.WriteMessage("Client = " + Assembly.GetEntryAssembly().FullName);
				progress.WriteMessage("Chorus = " + Assembly.GetExecutingAssembly().FullName);
			}
			catch (Exception)
			{
				progress.WriteWarning("Could not get all assembly info.");
			}

			progress.WriteMessage("---------------------------------------------------");
			progress.WriteMessage("heads:");
			progress.WriteMessage(GetTextFromQuery("heads", 30));

			if (GetHeads().Count() > 1)
			{
				progress.WriteError("This project has some 'changesets' which have not been merged together. If this is still true after Send/Receive, then you will need expert help to get things merging again.");
			}

			progress.WriteMessage("---------------------------------------------------");

			progress.WriteMessage("status:");
			progress.WriteMessage(GetTextFromQuery("status", 30));

			progress.WriteMessage("---------------------------------------------------");

			progress.WriteMessage("Log of last 100 changesets:");
			try
			{   //use glog if it is installd and enabled
				progress.WriteMessage(GetTextFromQuery("glog -l 100", 30));
			}
			catch (Exception)
			{
				progress.WriteMessage(GetTextFromQuery("log -l 100", 30));
			}
			progress.WriteMessage("---------------------------------------------------");

			progress.WriteMessage("config:");
			progress.WriteMessage(GetTextFromQuery("showconfig", 30));
			progress.WriteMessage("---------------------------------------------------");

			progress.WriteMessage("manifest:");
			progress.WriteMessage(GetTextFromQuery("manifest", 30));
			progress.WriteMessage("---------------------------------------------------");


			progress.WriteMessage(".hgignore");
			try
			{
				progress.WriteMessage(File.ReadAllText(Path.Combine(_pathToRepository, ".hgignore")));
			}
			catch (Exception error)
			{
				progress.WriteMessage("No .hgignore found");
			}
			progress.WriteMessage("---------------------------------------------------");

			progress.WriteMessage("hgrc");
			try
			{
				progress.WriteMessage(File.ReadAllText(Path.Combine(Path.Combine(_pathToRepository, ".hg"), "hgrc")));
			}
			catch (Exception error)
			{
				progress.WriteError("No .hg/hgrc found");
			}

			CheckIntegrity(progress);

			progress.WriteStatus("Done.");
		}

		public enum IntegrityResults { Good, Bad }

		public IntegrityResults CheckIntegrity(IProgress progress)
		{
			progress.WriteStatus("Validating Repository... (this can take a long time)");
			var result = GetTextFromQuery("verify", 60 * 60);
			if (result.ToLower().Contains("error"))
			{
				progress.WriteError(result);
				return IntegrityResults.Bad;
			}
			else
			{
				progress.WriteMessage(result);
				return IntegrityResults.Good;
			}
		}

		public string GetLog(int maxChangeSetsToShow)
		{
			if (maxChangeSetsToShow > 0)
			{
				return GetTextFromQuery(String.Format("log -G -l {0}", maxChangeSetsToShow));
			}
			else
			{
				return GetTextFromQuery("log -G");
			}
		}

		public void SetupEndOfLineConversion(IEnumerable<string> extensionsOfKnownTextFileTypes)
		{
			var doc = GetHgrcDoc();
			doc.Sections.RemoveSection("encode");//clear it out
			var section = doc.Sections.GetOrCreate("encode");
			foreach (string extension in extensionsOfKnownTextFileTypes)
			{
				string ext = extension.TrimStart(new char[] { '.' });
				section.Set("**." + ext, "dumbencode:");
			}
			doc.SaveAndThrowIfCannot();
		}

		/// <summary>
		/// NB: this adds a new changeset
		/// </summary>
		/// <param name="number"></param>
		/// <param name="tag"></param>
		public void TagRevision(string revisionNumber, string tag)
		{
			Execute(false, SecondsBeforeTimeoutOnLocalOperation, "tag -r " + revisionNumber + " \"" + tag + "\"");
		}

		protected static string SurroundWithQuotes(string path)
		{
			return "\"" + path + "\"";
		}

		/// <summary>
		/// Does a backout of the specified revision, which must be the head of its branch
		/// (this simplifies things, because we don't have to worry about non-trivial merging of the
		/// backout changeset).
		/// Afterwards, the current head will be the backout revision.
		/// </summary>
		/// <returns>The local revision # of the backout changeset (which will always be tip)</returns>
		public string BackoutHead(string revisionNumber, string changeSetSummary)
		{
			if (GetHasOneOrMoreChangeSets())
			{
				Guard.Against(!GetIsHead(revisionNumber), "BackoutHead() requires that the specified revision be a head, because this is the only scenario which is handled and unit-tested.");

				var previousRevisionOfWorkingDir = GetRevisionWorkingSetIsBasedOn();

				Update(revisionNumber);//move over to this branch, if necessary

				using (var messageFile = new TempFile(changeSetSummary))
				{
					Execute(false, SecondsBeforeTimeoutOnLocalOperation,
							string.Format("backout -r {0} --logfile \"{1}\"", revisionNumber, messageFile.Path));
				}
				//if we were not backing out the "current" revision, move back over to it.
				if (!previousRevisionOfWorkingDir.GetMatchesLocalOrHash(revisionNumber))
				{
					Update(previousRevisionOfWorkingDir.Number.Hash);
				}
				return GetTip().Number.LocalRevisionNumber;
			}
			else //hg cannot "backout" the very first revision
			{
				//it's not clear what I should do
				throw new ApplicationException("Cannot backout the very first changeset.");
			}
		}

		private bool GetIsHead(string localOrHashNumber)
		{
			return GetHeads().Any(h => h.Number.LocalRevisionNumber == localOrHashNumber || h.Number.Hash == localOrHashNumber);
		}

		private bool GetHasOneOrMoreChangeSets()
		{
			return GetTip() != null;
		}

		/// <summary>
		/// Mercurial gives us a way to set proxy info in the hgrc or ini files, but that
		/// a) has to be noticed and set up prior to Send/Receive and
		/// b) may go in and out of correctness, as the user travels between network connections.
		/// c) leaves the credentials stored in clear text on the hard drive
		///
		/// So for now, we're going to see how it works out there in the world if we just always
		/// handle this ourselves, never paying attention to the hgrc/mercurial.ini
		/// </summary>
		public string GetProxyConfigParameterString(string httpUrl, IProgress progress)
		{
			if (!_haveLookedIntoProxySituation && !GetIsLocalUri(httpUrl))
			{
				_proxyCongfigParameterString = DoWorkOfDeterminingProxyConfigParameterString(httpUrl, progress);
				_haveLookedIntoProxySituation = true;
			}
			return _proxyCongfigParameterString;
		}


		public static string DoWorkOfDeterminingProxyConfigParameterString(string httpUrl, IProgress progress)
		{
			/* The hg url itself would be more robust for the theoretical possibility of different
				* proxies for different destinations, but some hg servers (notably language depot) require a login.
				* So we're ignoring what we were given, and just using a known address, for now.
				*/
			httpUrl = "http://hg.palaso.org";

			progress.WriteVerbose("Checking for proxy by trying to http-get {0}...", httpUrl);

			try
			{
				//The following, which comes from the palaso library, will take care of remembering, between runs,
				//what credentials the user entered.  If they are needed but are missing or don't work,
				//it will put a dialog asking for them.
				string hostAndPort;
				string userName;
				string password;
				if(!RobustNetworkOperation.DoHttpGetAndGetProxyInfo(httpUrl, out hostAndPort, out userName, out password,
					msg=>progress.WriteVerbose(msg)))
				{
					return string.Empty;
				}
				else
				{
					return MakeProxyConfigParameterString(hostAndPort.Replace("http://",""), userName, password);
				}
			}
			catch(Exception e)
			{
				progress.WriteWarning("Failed to determine if we need to use authentication for a proxy...");
				progress.WriteException(e);
				return " ";// space is safer when appending params than string.Empty;
			}
		}

		private static string MakeProxyConfigParameterString(string proxyHostAndPort, string proxyUserName, string proxyPassword)
		{
			var builder = new StringBuilder();
			builder.AppendFormat(" --config \"http_proxy.host={0}\" ", proxyHostAndPort);
			if(!string.IsNullOrEmpty(proxyUserName))
			{
				builder.AppendFormat(" --config \"http_proxy.user={0}\" ", proxyUserName);

				if (!string.IsNullOrEmpty(proxyPassword))
				{
					builder.AppendFormat(" --config \"http_proxy.passwd={0}\" ", proxyPassword);
				}
			}

			return builder.ToString();
		}


		/// <summary>
		/// Tells whether is looks like we have enough information to attempt an internet send/receive
		/// </summary>
		/// <param name="message">An english string which will convey the readiness status</param>
		/// <returns></returns>
		public bool GetIsReadyForInternetSendReceive(out string message)
		{
			var address = GetDefaultNetworkAddress<HttpRepositoryPath>();

			if (address==null || string.IsNullOrEmpty(address.URI))
			{
				message = "The address of the server is empty.";
				return false;
			}

			Uri uri;
			if (!Uri.TryCreate(address.URI, UriKind.Absolute, out uri))
			{
				message = "The address of the server has problems.";
				return false;
			}

			if (string.IsNullOrEmpty(uri.PathAndQuery))
			{
				message = string.Format("The project name at {0} is missing.", uri.Host);
				return false;
			}

			if (string.IsNullOrEmpty(address.UserName))
			{
				message = "The account name is missing.";
				return false;
			}

			if (string.IsNullOrEmpty(address.Password))
			{
				message = string.Format("The password for {0} is missing.", uri.Host);
				return false;
			}

			message = string.Format("Ready to send/receive to {0} with project '{1}' and user '{2}'",
				uri.Host, uri.PathAndQuery.Trim(new char[]{'/'}), address.UserName);


			return true;


		}
	}

}