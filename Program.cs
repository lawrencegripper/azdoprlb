using System;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System.Collections.Generic;
using System.Linq;

namespace adoprloadbalancer
{

    class PullRequest
    {
        public GitPullRequest PR;
        public List<IdentityRefWithVote> Reviewers;
    }

    class ReviewersHistory
    {
        public bool IsActive;
        public string UserId;
        public int PastReviewCount;
        public int ActiveReviewsCount;
        public int TotalReviews => (PastReviewCount + ActiveReviewsCount);
    }

    class Program
    {
        private const int VoteApprovedConst = 10;
        private const int NoVoteConst = 0;

        static void Main(string[] args)
        {
            if (args.Length == 3)
            {
                Uri orgUrl = new Uri(args[0]);         // Organization URL, for example: https://dev.azure.com/fabrikam               
                String personalAccessToken = args[1];  // See https://docs.microsoft.com/azure/devops/integrate/get-started/authentication/pats
                String project = args[2];              // Project name which this should look at

                // Create a connection
                VssConnection connection = new VssConnection(orgUrl, new VssBasicCredential(string.Empty, personalAccessToken));

                BuildStats(connection, project, targetReviewerCount: 2).Wait();
            }
            else
            {
                Console.WriteLine("Usage: ConsoleApp {orgUrl} {personalAccessToken} {projectName}");
            }

        }

        static private async Task BuildStats(VssConnection connection, String project, int targetReviewerCount)
        {
            var prs = await GetAllActivePRs(connection, project);

            Dictionary<string, ReviewersHistory> reviewersHistory = BuildHistory(prs);

            var prsNeedingReviewers = prs.Where(pr => pr.Reviewers.Count() < 2 && pr.PR.Status == PullRequestStatus.Active);

            foreach (var pr in prsNeedingReviewers)
            {
                while (pr.Reviewers.Count() < 2)
                {
                    var reviewerToAssign = GetNextReviewer(reviewersHistory.Values);
                    
                    if (reviewerToAssign == null) {
                        Console.WriteLine("We ran out of reviewers to assign. Will wait for next invocation and try agian");
                        Environment.Exit(1);
                    }

                    Console.WriteLine($"Would assign user {reviewerToAssign.UserId} to review {pr.PR.Title}");

                    // // Todo: Actually set user on PR
                    
                    // if (pr.Reviewers?.Count() > 0)
                    // {
                    //     pr.Reviewers.Add(new IdentityRefWithVote(new IdentityRef { Id = reviewerToAssign.UserId }));
                    // }
                    // else
                    // {
                    //     pr.Reviewers = new List<IdentityRefWithVote>() { new IdentityRefWithVote(new IdentityRef { Id = reviewerToAssign.UserId }) };
                    // }

                }

            }
        }

        static private ReviewersHistory GetNextReviewer(IEnumerable<ReviewersHistory> reviewersHistory)
        {
            var availableUsers = reviewersHistory.Where(user => user.ActiveReviewsCount < 2);
            var sortedByHistory = availableUsers.OrderBy(user => user.TotalReviews);

            var reviewerToAssign = sortedByHistory.FirstOrDefault();
            if (reviewerToAssign == null)
            {
                return null;
            }
            reviewerToAssign.ActiveReviewsCount++;

            return reviewerToAssign;
        }

        private static Dictionary<string, ReviewersHistory> BuildHistory(IEnumerable<PullRequest> prs)
        {
            var reviewersHistory = new Dictionary<string, ReviewersHistory>();
            var reviewerNameMap = new Dictionary<string, string>();
            var prCreators = new Dictionary<string, int>();

            // Build history 
            foreach (var pr in prs)
            {
                if (prCreators.ContainsKey(pr.PR.CreatedBy.DisplayName)) {
                    prCreators[pr.PR.CreatedBy.DisplayName] = prCreators[pr.PR.CreatedBy.DisplayName]+1;
                } else {
                    prCreators[pr.PR.CreatedBy.DisplayName] = 1;
                }
                foreach (var reviwer in pr.Reviewers)
                {
                    reviewerNameMap[reviwer.Id] = reviwer.DisplayName;
                    // Build `pastReviewCount`
                    if (pr.PR.Status == PullRequestStatus.Completed && reviwer.Vote > NoVoteConst)
                    {
                        if (reviewersHistory.ContainsKey(reviwer.Id))
                        {
                            reviewersHistory[reviwer.Id].PastReviewCount++;
                        }
                        else
                        {
                            reviewersHistory[reviwer.Id] = new ReviewersHistory
                            {
                                UserId = reviwer.Id,
                                PastReviewCount = 1,
                            };
                        }
                    }

                    // Build `activeReviewCount`
                    if (pr.PR.Status == PullRequestStatus.Active)
                    {
                        if (reviewersHistory.ContainsKey(reviwer.Id))
                        {
                            reviewersHistory[reviwer.Id].ActiveReviewsCount++;
                        }
                        else
                        {
                            reviewersHistory[reviwer.Id] = new ReviewersHistory
                            {
                                UserId = reviwer.Id,
                                ActiveReviewsCount = 1,
                            };
                        }
                    }
                }
            }

            Console.WriteLine(prs.Count());
            
            Console.WriteLine("PR Reviews by User");
            var revSorted = reviewersHistory.OrderByDescending(x=>x.Value.PastReviewCount);
            foreach (var item in revSorted) {              
                Console.WriteLine($"{item.Value.PastReviewCount}  {(prs.Count() / 100) * item.Value.PastReviewCount}%  {reviewerNameMap[item.Key]}");
            }
            
            Console.WriteLine("PRs Submitted by Author");
            var prsSorted = prCreators.OrderByDescending(x=>x.Value);
            var prsTotal = prCreators.Select(x=>x.Value).Sum();
            foreach (var item in prsSorted) {
                Console.WriteLine($"{item.Value}  {(prsTotal / 100) * item.Value}%  {item.Key}");
            }

            return reviewersHistory;
        }

        static private async Task<IEnumerable<PullRequest>> GetAllActivePRs(VssConnection connection, String project)
        {
            var sourceClient = connection.GetClient<GitHttpClient>();
            var repos = await sourceClient.GetRepositoriesAsync(project);

            // var allPRs = new List<GitPullRequest>();
            // var querySettings = new GitPullRequestSearchCriteria();
            // // querySettings.Status = PullRequestStatus.All;

            var allPRs = new List<PullRequest>();

            foreach (var repo in repos)
            {

                Console.WriteLine($"Getting all Active PRs for '{repo.Name}'");

                var prs = await sourceClient.GetPullRequestsAsync(project, repo.Id.ToString(), new GitPullRequestSearchCriteria() { Status = PullRequestStatus.All, IncludeLinks = true });
                foreach (var pr in prs)
                {
                    Console.WriteLine($"Getting reviewers for '{pr.Title}'");

                    var reviews = await sourceClient.GetPullRequestReviewersAsync(repo.Id.ToString(), pr.PullRequestId);
                    allPRs.Add(new PullRequest
                    {
                        PR = pr,
                        Reviewers = reviews
                    });
                }

            }

            Console.WriteLine("Got PRs");
            return allPRs;
        }
    }
}
