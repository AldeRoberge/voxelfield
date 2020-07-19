using Amazon.GameLift;
using Amazon.GameLift.Model;
using Amazon.Runtime;
using UnityEngine;

namespace Voxelfield
{
    public class GameLiftClientBehavior : MonoBehaviour
    {
        private void Start()
        {
            var config = new AmazonGameLiftConfig();
            var credentials = new BasicAWSCredentials(@"AKIAWKQVDVRWQMYAUBAV", @"62ixippCgELFUDKgPlGnWqtd0WEZ3w51YhEnMK8C");
            var client = new AmazonGameLiftClient(credentials, config);
            var searchRequest = new SearchGameSessionsRequest {Limit = 1};
            SearchGameSessionsResponse sessions = client.SearchGameSessions(searchRequest);
            Debug.Log(sessions.GameSessions.Count);
            foreach (GameSession gameSession in sessions.GameSessions)
            {
            }
        }

        private void OnApplicationPause(bool pauseStatus) { }
    }
}