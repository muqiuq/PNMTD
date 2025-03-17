﻿using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace PNMTD.Lib.Authentification
{
    public class JwtTokenHelper
    {
        public static byte[] ExpandKey(string keyStr)
        {
            byte[] key;
            using (SHA512 sha512 = SHA512.Create())
            {
                key = sha512.ComputeHash(Encoding.ASCII.GetBytes(keyStr));
            }

            return key;
        }

        public static string GenerateNewToken(string username, string issuer, string audience, string keystr,
            int validForMonths = 12, int validForMinutes = 5)
        {
            var key = ExpandKey(keystr);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                new Claim("Id", Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Sub, username),
                new Claim(JwtRegisteredClaimNames.Email, username),
                new Claim(JwtRegisteredClaimNames.Jti,
                Guid.NewGuid().ToString())
             }),
                Expires = DateTime.UtcNow.AddMonths(validForMonths).AddMinutes(validForMinutes),
                Issuer = issuer,
                Audience = audience,
                SigningCredentials = new SigningCredentials
                (new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha512Signature)
            };
            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);
            var jwtToken = tokenHandler.WriteToken(token);
            var stringToken = tokenHandler.WriteToken(token);
            return stringToken;
        }

        public static string GenerateNewToken(IConfiguration configuration, string username, int validForMonths = 12, int validForMinutes = 5)
        {
            var issuer = configuration["Jwt:Issuer"];
            var audience = configuration["Jwt:Audience"];
            var keystr = configuration["Jwt:Key"];
            return GenerateNewToken(username, issuer, audience, keystr, validForMonths, validForMinutes);
        }
    }
}
