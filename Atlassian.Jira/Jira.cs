﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Atlassian.Jira.Remote;
using Atlassian.Jira.Linq;
using System.ServiceModel;
using RestSharp;
using Newtonsoft.Json.Linq;

namespace Atlassian.Jira
{
    /// <summary>
    /// Represents a JIRA server
    /// </summary>
    public class Jira
    {
        private const int DEFAULT_MAX_ISSUES_PER_REQUEST = 20;
        private const string ALL_PROJECTS_KEY = "[ALL_PROJECTS]";
        private const string REMOTE_AUTH_EXCEPTION_STRING = "com.atlassian.jira.rpc.exception.RemoteAuthenticationException";

        private readonly JiraQueryProvider _provider;
        private readonly IJiraRemoteService _jiraRemoteService;
        private readonly IFileSystem _fileSystem;
        private readonly string _username = null;
        private readonly string _password = null;
        private readonly bool _isAnonymous = false;

        private string _token = String.Empty;
        private Dictionary<string, IEnumerable<ProjectVersion>> _cachedVersions = new Dictionary<string,IEnumerable<ProjectVersion>>();
        private Dictionary<string, IEnumerable<ProjectComponent>> _cachedComponents = new Dictionary<string, IEnumerable<ProjectComponent>>();
        private Dictionary<string, IEnumerable<JiraNamedEntity>> _cachedFieldsForEdit = new Dictionary<string, IEnumerable<JiraNamedEntity>>();
        private Dictionary<string, IEnumerable<IssueType>> _cachedIssueTypes = new Dictionary<string, IEnumerable<IssueType>>();
        private IEnumerable<JiraNamedEntity> _cachedCustomFields = null;
        private IEnumerable<JiraNamedEntity> _cachedFilters = null;
        private IEnumerable<IssuePriority> _cachedPriorities = null;
        private IEnumerable<IssueStatus> _cachedStatuses = null;
        private IEnumerable<IssueResolution> _cachedResolutions = null;
        private IEnumerable<Project> _cachedProjects = null;

        /// <summary>
        /// Create a connection to a JIRA server with anonymous access
        /// </summary>
        /// <param name="url">Url to the JIRA server</param>
        public Jira(string url)
            : this(url, null, null)
        {
        }

        /// <summary>
        /// Create a connection to a JIRA server with provided credentials
        /// </summary>
        /// <param name="url">Url to the JIRA server</param>
        /// <param name="username">username to use to authenticate</param>
        /// <param name="password">passowrd to use to authenticate</param>
        public Jira(string url, string username, string password)
            :this(new JqlExpressionVisitor(),
                  new JiraRemoteServiceWrapper(url, username, password),
                  new FileSystem(),
                  username, 
                  password)
        {
        }

        /// <summary>
        /// Create a connection to a JIRA server
        /// </summary>
        public Jira(IJqlExpressionVisitor translator, 
                    IJiraRemoteService jiraRemoteService, 
                    IFileSystem fileSystem,
                    string username, 
                    string password)
        {
            _username = username;
            _password = password;
            _isAnonymous = String.IsNullOrEmpty(username) && String.IsNullOrEmpty(password);
            _jiraRemoteService = jiraRemoteService;
            _fileSystem = fileSystem;
            this.MaxIssuesPerRequest = DEFAULT_MAX_ISSUES_PER_REQUEST;
            this.Debug = false;

            this._provider = new JiraQueryProvider(translator, this);
        }

        internal IJiraRemoteService RemoteService
        {
            get
            {
                return _jiraRemoteService;
            }
        }

        /// <summary>
        /// Whether to print the translated JQL to console
        /// </summary>
        public bool Debug { get; set; }

        /// <summary>
        /// Whether to use the JIRA Rest API when querying for issues
        /// </summary>
        /// <remarks>
        /// Enables support for server side Count() and Skip() processing
        /// </remarks>
        public bool UseRestApi { get; set; }

        /// <summary>
        /// Maximum number of issues per request
        /// </summary>
        public int MaxIssuesPerRequest { get; set; }

        /// <summary>
        /// Url to the JIRA server
        /// </summary>
        public string Url
        {
            get { return _jiraRemoteService.Url; }
        }

        internal string UserName
        {
            get { return _username; }
        }

        internal string Password
        {
            get { return _password; }
        }

        internal IFileSystem FileSystem
        {
            get { return _fileSystem; }
        } 

        /// <summary>
        /// Query the issues database
        /// </summary>
        /// <returns>IQueryable of Issue</returns>
        public JiraQueryable<Issue> Issues
        {
            get
            {
                return new JiraQueryable<Issue>(_provider);
            }
        }

        /// <summary>
        /// Gets an issue from the JIRA server
        /// </summary>
        /// <param name="key">The key of the issue</param>
        /// <returns></returns>
        public Issue GetIssue(string key)
        {
            return (from i in Issues
                    where i.Key == key
                    select i).First();
        }

        /// <summary>
        /// Returns issues that match the specified filter
        /// </summary>
        /// <param name="filterName">The name of the filter used for the search</param>
        /// <param name="start">The place in the result set to use as the first issue returned</param>
        /// <param name="maxResults">The maximum number of issues to return</param>
        public IEnumerable<Issue> GetIssuesFromFilter(string filterName, int start = 0, int? maxResults = null)
        {
            var filter = this.GetFilters().FirstOrDefault(f => f.Name.Equals(filterName, StringComparison.OrdinalIgnoreCase));

            if (filter == null)
            {
                throw new InvalidOperationException(String.Format("Filter with name '{0}' was not found", filterName));
            }

            return WithToken(token =>
            {
                return _jiraRemoteService.GetIssuesFromFilterWithLimit(token, filter.Id, start, maxResults ?? this.MaxIssuesPerRequest).Select(i => new Issue(this, i));
            });
        }

        /// <summary>
        /// Execute a specific JQL query and return the resulting issues
        /// </summary>
        /// <param name="jql">JQL search query</param>
        /// <returns>Collection of Issues that match the search query</returns>
        public IEnumerable<Issue> GetIssuesFromJql(string jql)
        {
            return GetIssuesFromJql(jql, null);
        }

        /// <summary>
        /// Execute a specific JQL query and return the resulting issues
        /// </summary>
        /// <param name="jql">JQL search query</param>
        /// <param name="maxResults">Maximum number of issues to return</param>
        /// <returns>Collection of Issues that match the search query</returns>
        public IEnumerable<Issue> GetIssuesFromJql(string jql, int? maxResults, int startAt = 0)
        {
            if (this.Debug)
            {
                Console.WriteLine("JQL: " + jql);
            }

            IList<Issue> issues = new List<Issue>();

            if (UseRestApi)
            {
                var json = JObject.Parse(_jiraRemoteService.GetJsonFromJqlSearch(jql, startAt, maxResults?? MaxIssuesPerRequest));

                foreach(var issue in (JArray)json["issues"])
                {
                    issues.Add(Issue.FromJson(this, issue.ToString()));
                }
            }
            else if (startAt == 0)
            {
                WithToken(t =>
                {
                    foreach (RemoteIssue remoteIssue in _jiraRemoteService.GetIssuesFromJqlSearch(t, jql, maxResults ?? MaxIssuesPerRequest))
                    {
                        issues.Add(new Issue(this, remoteIssue));
                    }
                });
            }
            else
            {
                throw new InvalidOperationException("'StartAt' cannot be used with SOAP API, set Jira.UseRestApi instead");
            }

            return issues;
        }

        /// <summary>
        /// Returns a new issue that when saved will be created on the remote JIRA server
        /// </summary>
        public Issue CreateIssue(string project, string parentIssueKey = null)
        {
            return new Issue(this, project, parentIssueKey);
        }

        /// <summary>
        /// Returns all the issue types within JIRA
        /// </summary>
        /// <param name="projectKey">If provided, returns issue types only for given project</param>
        /// <returns>Collection of JIRA issue types</returns>
        public IEnumerable<IssueType> GetIssueTypes(string projectKey = null)
        {
            string projectId = null;
            if (projectKey != null)
            {
                var project = this.GetProjects().FirstOrDefault(p => p.Key.Equals(projectKey, StringComparison.OrdinalIgnoreCase));
                if (project != null)
                {
                    projectId = project.Id;
                }
            }

            projectKey = projectKey ?? ALL_PROJECTS_KEY;

            if (!_cachedIssueTypes.ContainsKey(projectKey))
            {
                WithToken(token =>
                {
                    _cachedIssueTypes.Add(projectKey, _jiraRemoteService.GetIssueTypes(token, projectId).Select(t => new IssueType(t)));
                });
            }

            return _cachedIssueTypes[projectKey];
        }

        /// <summary>
        /// Returns all versions defined on a JIRA project
        /// </summary>
        /// <param name="projectKey">The project to retrieve the versions from</param>
        /// <returns>Collection of JIRA versions.</returns>
        public IEnumerable<ProjectVersion> GetProjectVersions(string projectKey)
        {
            if (!_cachedVersions.ContainsKey(projectKey))
            {
                WithToken(token =>
                {
                    _cachedVersions.Add(projectKey, _jiraRemoteService.GetVersions(token, projectKey).Select(v => new ProjectVersion(v)));
                });
            }

            return _cachedVersions[projectKey];            
        }

        /// <summary>
        /// Returns all components defined on a JIRA project
        /// </summary>
        /// <param name="projectKey">The project to retrieve the components from</param>
        /// <returns>Collection of JIRA components</returns>
        public IEnumerable<ProjectComponent> GetProjectComponents(string projectKey)
        {
            if (!_cachedComponents.ContainsKey(projectKey))
            {
                WithToken(token =>
                {
                    _cachedComponents.Add(projectKey, _jiraRemoteService.GetComponents(token, projectKey).Select(c => new ProjectComponent(c)));
                });
            }

            return _cachedComponents[projectKey];
        }

        /// <summary>
        /// Returns all the issue priorities within JIRA
        /// </summary>
        /// <returns>Collection of JIRA issue priorities</returns>
        public IEnumerable<IssuePriority> GetIssuePriorities()
        {
            if (_cachedPriorities == null)
            {
                WithToken(token =>
                {
                    _cachedPriorities = _jiraRemoteService.GetPriorities(token).Select(p => new IssuePriority(p));
                });
            }

            return _cachedPriorities;
        }

        /// <summary>
        /// Returns all the issue statuses within JIRA
        /// </summary>
        /// <returns>Collection of JIRA issue statuses</returns>
        public IEnumerable<IssueStatus> GetIssueStatuses()
        {
            if (_cachedStatuses == null)
            {
                WithToken(token =>
                {
                    _cachedStatuses = _jiraRemoteService.GetStatuses(token).Select(s => new IssueStatus(s));
                });
            }

            return _cachedStatuses;
        }

        /// <summary>
        /// Returns all the issue resolutions within JIRA
        /// </summary>
        /// <returns>Collection of JIRA issue resolutions</returns>
        public IEnumerable<IssueResolution> GetIssueResolutions()
        {
            if (_cachedResolutions == null)
            {
                WithToken(token =>
                {
                    _cachedResolutions = _jiraRemoteService.GetResolutions(token).Select(r => new IssueResolution(r));
                });
            }

            return _cachedResolutions;
        }

        /// <summary>
        /// Returns all custom fields within JIRA
        /// </summary>
        /// <returns>Collection of JIRA custom fields</returns>
        public IEnumerable<JiraNamedEntity> GetCustomFields()
        {
            if (_cachedCustomFields == null)
            {
                WithToken(token =>
                {
                    _cachedCustomFields = _jiraRemoteService.GetCustomFields(token).Select(f => new JiraNamedEntity(f));
                });
            }
            return _cachedCustomFields;
        }

        /// <summary>
        /// Returns the favourite filters for the user
        /// </summary>
        public IEnumerable<JiraNamedEntity> GetFilters()
        {
            if (_cachedFilters == null)
            {
                WithToken(token =>
                {
                    _cachedFilters = _jiraRemoteService.GetFavouriteFilters(token).Select(f => new JiraNamedEntity(f));
                });
            }

            return _cachedFilters;
        }

        /// <summary>
        /// Returns all projects defined in JIRA
        /// </summary>
        public IEnumerable<Project> GetProjects()
        {
            if (_cachedProjects == null)
            {
                WithToken(token =>
                {
                    _cachedProjects = _jiraRemoteService.GetProjects(token).Select(p => new Project(p));
                });
            }

            return _cachedProjects;
        }

        /// <summary>
        /// Executes an action using the user's authentication token.
        /// </summary>
        /// <remarks>
        /// If action fails with 'com.atlassian.jira.rpc.exception.RemoteAuthenticationException'
        /// a new token will be requested from server and the action called again.
        /// </remarks>
        public void WithToken(Action<string> action)
        {
            WithToken<object>(t =>
            {
                action(t);
                return null;
            });
        }

        /// <summary>
        /// Executes an action using the user's authentication token and the jira soap client
        /// </summary>
        /// <remarks>
        /// If action fails with 'com.atlassian.jira.rpc.exception.RemoteAuthenticationException'
        /// a new token will be requested from server and the action called again.
        /// </remarks>
        public void WithToken(Action<string, IJiraRemoteService> action)
        {
            WithToken<object>((token, client) =>
            {
                action(token, client);
                return null;
            });
        }

        /// <summary>
        /// Executes a function using the user's authentication token.
        /// </summary>
        /// <remarks>
        /// If function fails with 'com.atlassian.jira.rpc.exception.RemoteAuthenticationException'
        /// a new token will be requested from server and the function called again.
        /// </remarks>
        public TResult WithToken<TResult>(Func<string, TResult> function)
            where TResult: class
        {
            return WithToken((token, client) => function(token));
        }

        /// <summary>
        /// Executes a function using the user's authentication token and the jira soap client
        /// </summary>
        /// <remarks>
        /// If function fails with 'com.atlassian.jira.rpc.exception.RemoteAuthenticationException'
        /// a new token will be requested from server and the function called again.
        /// </remarks>
        public TResult WithToken<TResult>(Func<string, IJiraRemoteService, TResult> function)
        {
            if (!_isAnonymous && String.IsNullOrEmpty(_token))
            {
                _token = _jiraRemoteService.Login(_username, _password);
            }

            try
            {
                return function(_token, this.RemoteService);
            }
            catch (FaultException fe)
            {
                if (_isAnonymous
                    || fe.Message.IndexOf(REMOTE_AUTH_EXCEPTION_STRING, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw;
                }

                _token = _jiraRemoteService.Login(_username, _password);
                return function(_token, this.RemoteService);
            }
        }

        internal IEnumerable<JiraNamedEntity> GetFieldsForEdit(string projectKey)
        {
            if (!_cachedFieldsForEdit.ContainsKey(projectKey))
            {
                var tempIssue = this.GetIssuesFromJql(
                                        String.Format("project = \"{0}\"", projectKey), 
                                        1).FirstOrDefault();

                if (tempIssue == null)
                {
                    throw new InvalidOperationException("Project must contain at least one issue to be able to retrieve issue fields.");
                }

                WithToken(token =>
                {
                    _cachedFieldsForEdit.Add(projectKey, _jiraRemoteService.GetFieldsForEdit(token, tempIssue.Key.Value).Select(f => new JiraNamedEntity(f)));
                });
            }

            return _cachedFieldsForEdit[projectKey];
        }
    }
}
