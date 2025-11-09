using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DeezFiles.Services
{
    public class AuthorizationService
    {
        public static string nodeAddress;

        public static async Task<bool> MakeNodeOnline()
        {
            HttpClient GoOnlineclient = new HttpClient();
            var goonline = new
            {
                DNAddress = nodeAddress
            };

            string goOnlinejson = JsonConvert.SerializeObject(goonline);
            var goOnlinecontent = new StringContent(goOnlinejson, Encoding.UTF8, "application/json");

            HttpResponseMessage onlineResponse = await NetworkService.SendPostRequest("OnlineNodes/GoOnline", goOnlinecontent);
            if (onlineResponse.IsSuccessStatusCode) { return true; }
            return false;
        }

        public static async Task<string> LoginUser(string username, string password)
        {
            var loginuser = new
            {
                username = username,
                password = password
            };

            string jsondata = JsonConvert.SerializeObject(loginuser);
            var content = new StringContent(jsondata, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await NetworkService.SendPostRequest("LoginInfoes/login", content);


            if (response.IsSuccessStatusCode)
            {

                string selfAddress = await response.Content.ReadAsStringAsync();
                nodeAddress = selfAddress;
                bool isOnline = await MakeNodeOnline();
                string result = isOnline ? selfAddress : "None";
                return result;
            }
            return "None";
        }

        public static async Task<HttpResponseMessage> RegisterUser(string username, string password, string email)
        {

            var newUser = new
            {
                Username = username,
                DNAddress = "none",
                EmailId = email,
                Password = password
            };

            string jsondata = JsonConvert.SerializeObject(newUser);
            var content = new StringContent(jsondata, Encoding.UTF8, "application/json");
            HttpResponseMessage postresponse = await NetworkService.SendPostRequest("LoginInfoes/register", content);
            return postresponse;
        }

        public static async Task<HttpResponseMessage> Logout()
        {
            HttpClient client = new HttpClient();
            var offlineData = new
            {
                DNAddress = nodeAddress
            };

            string offlineJson = JsonConvert.SerializeObject(offlineData);
            var content = new StringContent(offlineJson, Encoding.UTF8, "application/json");
            HttpResponseMessage response = await NetworkService.SendPostRequest("OnlineNodes/GoOffline", content);
            return response;
        }
    }
}
