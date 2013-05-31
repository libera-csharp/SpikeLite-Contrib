/**
 * SpikeLite C# IRC Bot
 * Copyright (c) 2013 FreeNode ##Csharp Community
 * 
 * This source is licensed under the terms of the MIT license. Please see 
 * https://github.com/Freenode-Csharp/SpikeLite/ for details.
 */

using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net;
using System.Net.Http;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Github_WebHook_Shunt.MessagingService;
using Newtonsoft.Json.Linq;
using log4net;
using System.Linq;

namespace Github_WebHook_Shunt
{
    /// <summary>
    /// Replicates some of the functionality we used to get from our CIA.vc instance. This is (hopefully) temporary and uses a crude shunt to parse messages and then pass 
    /// them onto our bot via one of the IPC endpoints we've got using WCF.
    /// </summary>
    /// 
    /// <remarks>
    /// Some examples from earlier commits:
    /// 
    /// (CIA-67)	SpikeLite: Greg master * r86445ed / (2 files): Remove mono-specific config (empty), add something about PGSQL - http://bit.ly/lFK7ws
    /// (CIA-67)	SpikeLite: Greg master * rae57f33 / (13 files in 7 dirs): Add the ability to correlate nicks with factoids. - http://bit.ly/myZUnI
    /// (CIA-67)>	SpikeLite: Greg master * rafa963b / .gitignore : Add beans-overrides.xml to the .gitignore so people don't accidentally commit API keys - http://bit.ly/l42lVN
    /// </remarks>
    class Program
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));
        private static readonly String BotEmail = ConfigurationManager.AppSettings["bot.email"];
        private static readonly String BotToken = ConfigurationManager.AppSettings["bot.token"];
        private static readonly String TargetChannel = ConfigurationManager.AppSettings["bot.channel"];
        private static MessagingServiceClient _messageServiceClient;

        public static void Main(string[] args)
        {
            log4net.Config.XmlConfigurator.Configure();
            _messageServiceClient = new MessagingServiceClient();

            Start();
        }

        private static void Start()
        {
            var port = ConfigurationManager.AppSettings["host.port"];
            Console.WriteLine("Listening on port {0}...", port);

            using (var shutdownEvent = new ManualResetEventSlim(false))
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(string.Format("http://*:{0}/", port));
                listener.Start();

                // ReSharper disable AccessToDisposedClosure
                Console.CancelKeyPress += ((sender, args) => shutdownEvent.Set());
                // ReSharper restore AccessToDisposedClosure

                while (!shutdownEvent.IsSet)
                {
                    var context = listener.GetContext();
                    Task.Factory.StartNew(() => HandleRequest(context));
                }                
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                if (request.HttpMethod.Equals("POST", StringComparison.InvariantCultureIgnoreCase) && request.HasEntityBody)
                {
                    var githubEvent = request.Headers["X-Github-Event"];

                    if (null != githubEvent && githubEvent.Equals("push", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var payload = WebUtility.UrlDecode(new StreamReader(request.InputStream).ReadToEnd());
                        ParsePayload(payload.Contains("payload=") ? payload.Substring(8) : payload);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Ruh-roh", ex);
            }
            finally
            {
                response.StatusCode = 202;
                response.Close();
            }
        }

        private static void ParsePayload(string payload)
        {
            var parsedPayload = JObject.Parse(payload);

            var repoName = parsedPayload["repository"]["name"].Value<String>();
            var branch = parsedPayload["ref"].Value<String>().Split('/').Last();
            var repoPrefix = parsedPayload["repository"]["url"].Value<String>();

            foreach (var commit in parsedPayload["commits"])
            {
                var authorName = commit["author"]["name"].Value<String>();
                var hash = commit["id"].Value<String>().Substring(0, 7);
                var message = commit["message"].Value<String>();
                var url = ShortenUrl(string.Format("{0}/commit/{1}", repoPrefix, hash));

                var botMessage = String.Format("{0}: {1} {2} * {3} / {4}: {5} - {6}", repoName, authorName, branch, hash, FormatFiles(commit), message, url);
                SendMessage(botMessage.Replace("\n", ""));
            }

            Log.Info(string.Format("Commit: {0}", payload));
        }

        /// <remarks>
        /// See also: https://github.com/blog/985-git-io-github-url-shortener.
        /// </remarks>
        private static object ShortenUrl(string url)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var content = new StringContent(String.Format("url={0}", WebUtility.UrlEncode(url)), Encoding.UTF8, "application/x-www-form-urlencoded");
                    var response = client.PostAsync("http://git.io", content).Result;
                    
                    return response.Headers.Location;    
                }
            }
            catch (Exception ex)
            {
                Log.Warn(String.Format("Failed to shorten URL {0}", url), ex);
            }

            return url;
        }

        private static void SendMessage(string botMessage)
        {
            using (new OperationContextScope(_messageServiceClient.InnerChannel))
            {
                var emailHeader = new MessageHeader<string>(BotEmail);
                var tokenHeader = new MessageHeader<string>(BotToken);

                OperationContext.Current.OutgoingMessageHeaders.Add(emailHeader.GetUntypedHeader("String", "net.freenode-csharp.auth.email"));
                OperationContext.Current.OutgoingMessageHeaders.Add(tokenHeader.GetUntypedHeader("String", "net.freenode-csharp.auth.token"));

                _messageServiceClient.SendMessage(TargetChannel, botMessage);
            }            
        }

        /// <summary>
        /// Attempts to gather all the adds/removals/updates into a coherent format that looks like the CIA.vc output as described in the file header.
        /// </summary>
        /// 
        /// <param name="commit">The JSON blob of our commit message.</param>
        /// 
        /// <returns>A formatted string of commit information, fit for display.</returns>
        private static string FormatFiles(JToken commit)
        {
            return ConcatFiles(commit["added"].Values<string>().Concat(commit["removed"].Values<string>())
                                                               .Concat(commit["modified"].Values<string>()).ToList());
        }

        /// <remarks>
        /// This was more or less reverse engineered from messages sent via the CIA.vc service. There seem to be three cases: singular files (where the literal
        /// filename is passed), multiple files in the same dir (N files committed) and multiple files in multiple dirs (N files in M dirs committed). There are
        /// probably more that should be added, but this is just a cheap hack anyway...
        /// </remarks>
        private static string ConcatFiles(List<String> files)
        {
            if (files.Count() == 1)
            {
                return files.First();
            }

            var directories = new HashSet<string>();

            foreach (var file in files)
            {
                var index = file.LastIndexOf("/", StringComparison.Ordinal);
                directories.Add(index > -1 ? file.Substring(0, index) : "/");
            }

            return String.Format("({0} files{1})", files.Count(), (directories.Count > 1) ? string.Format(" in {0} dirs", directories.Count) : "");
        }
    }
}
