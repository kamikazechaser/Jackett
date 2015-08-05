﻿using AutoMapper;
using Jackett.Models;
using Jackett.Services;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;

namespace Jackett.Controllers
{
    [AllowAnonymous]
    public class TorznabController : ApiController
    {
        private IIndexerManagerService indexerService;
        private Logger logger;
        private IServerService serverService;
        private ICacheService cacheService;

        public TorznabController(IIndexerManagerService i, Logger l, IServerService s, ICacheService c)
        {
            indexerService = i;
            logger = l;
            serverService = s;
            cacheService = c;
        }

        [HttpGet]
        public async Task<HttpResponseMessage> Call(string indexerID)
        {
            var indexer = indexerService.GetIndexer(indexerID);
            var torznabQuery = TorznabQuery.FromHttpQuery(HttpUtility.ParseQueryString(Request.RequestUri.Query));

            if (string.Equals(torznabQuery.QueryType, "caps", StringComparison.InvariantCultureIgnoreCase))
            {
                return new HttpResponseMessage()
                {
                    Content = new StringContent(indexer.TorznabCaps.ToXml(), Encoding.UTF8, "application/rss+xml")
                };
            }

            var allowBadApiDueToDebug = false;
#if DEBUG
            allowBadApiDueToDebug = Debugger.IsAttached;
#endif

            if (!allowBadApiDueToDebug && !string.Equals(torznabQuery.ApiKey, serverService.Config.APIKey, StringComparison.InvariantCultureIgnoreCase))
            {
                logger.Warn(string.Format("A request from {0} was made with an incorrect API key.", Request.GetOwinContext().Request.RemoteIpAddress));
                return Request.CreateResponse(HttpStatusCode.Forbidden, "Incorrect API key");
            }

            if (!indexer.IsConfigured)
            {
                logger.Warn(string.Format("Rejected a request to {0} which is unconfigured.", indexer.DisplayName));
                return Request.CreateResponse(HttpStatusCode.Forbidden, "This indexer is not configured.");
            }

            var releases = await indexer.PerformQuery(torznabQuery);

            // Cache non query results
            if (string.IsNullOrEmpty(torznabQuery.SanitizedSearchTerm))
            {
                cacheService.CacheRssResults(indexer, releases);
            }

            releases = indexer.FilterResults(torznabQuery, releases);

            // Log info
            if (string.IsNullOrWhiteSpace(torznabQuery.SanitizedSearchTerm))
            {
                logger.Info(string.Format("Found {0} releases from {1}", releases.Count(), indexer.DisplayName));
            }
            else
            {
                logger.Info(string.Format("Found {0} releases from {1} for: {2} {3}", releases.Count(), indexer.DisplayName, torznabQuery.SanitizedSearchTerm, torznabQuery.GetEpisodeSearchString()));
            }

            var severUrl = string.Format("{0}://{1}:{2}/", Request.RequestUri.Scheme, Request.RequestUri.Host, Request.RequestUri.Port);
            var resultPage = new ResultPage(new ChannelInfo
            {
                Title = indexer.DisplayName,
                Description = indexer.DisplayDescription,
                Link = new Uri(indexer.SiteLink),
                ImageUrl = new Uri(severUrl + "logos/" + indexer.ID + ".png"),
                ImageTitle = indexer.DisplayName,
                ImageLink = new Uri(indexer.SiteLink),
                ImageDescription = indexer.DisplayName
            });

            resultPage.Releases.AddRange(releases.Select(s=>Mapper.Map<ReleaseInfo>(s).ConvertToProxyLink(severUrl, indexerID)));
            var xml = resultPage.ToXml(new Uri(severUrl));
            // Force the return as XML
            return new HttpResponseMessage()
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/rss+xml")
            };
        }


    }
}