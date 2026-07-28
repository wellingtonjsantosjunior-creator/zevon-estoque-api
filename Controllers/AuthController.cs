using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using ZevonEstoque.Models;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly string _connectionString;
    private readonly IConfiguration _config;

    public AuthController(string connectionString, IConfiguration config)
    {
        _connectionString = connectionString;
        _config = config;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Senha))
            return BadRequest("Informe o login e a senha.");

        using var conn = new NpgsqlConnection(_connectionString);

        var usuario = await conn.QueryFirstOrDefaultAsync<Usuario>(@"
            SELECT 
                u.id_usuario   AS IdUsuario,
                u.id_filial    AS IdFilial,
                u.nome         AS Nome,
                u.email        AS Email,
                u.senha_hash   AS SenhaHash,
                u.perfil       AS Perfil,
                u.ativo        AS Ativo,
                u.criado_em    AS CriadoEm,
                u.primeiro_acesso AS PrimeiroAcesso,
                u.IdEmpresa    AS IdEmpresa
            FROM Usuarios u
            WHERE u.email = @Email
              AND u.ativo = true",
            new { Email = request.Email.Trim() });

        if (usuario == null)
            return Unauthorized("Usuário não encontrado.");

        var senhaHash = GerarHash(request.Senha.Trim());

        if (!string.Equals(usuario.SenhaHash, senhaHash, StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Senha incorreta.");

        var token = GerarToken(usuario);

        return Ok(new LoginResponse
        {
            Token = token,
            IdUsuario = usuario.IdUsuario,
            Nome = usuario.Nome,
            Perfil = usuario.Perfil,
            IdFilial = usuario.IdFilial ?? 0,
            IdEmpresa = usuario.IdEmpresa,
            PrimeiroAcesso = usuario.PrimeiroAcesso
        });
    }

    [HttpPost("redefinir-senha")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<IActionResult> RedefinirSenha([FromBody] RedefinirSenhaRequest request)
    {
        if (request.IdUsuario <= 0 || string.IsNullOrWhiteSpace(request.NovaSenha))
            return BadRequest("Dados inválidos.");

        using var conn = new NpgsqlConnection(_connectionString);

        var senhaHash = GerarHash(request.NovaSenha.Trim());

        await conn.ExecuteAsync(@"
            UPDATE Usuarios 
            SET senha_hash = @SenhaHash,
                primeiro_acesso = 0
            WHERE id_usuario = @IdUsuario",
            new { SenhaHash = senhaHash, IdUsuario = request.IdUsuario });

        return Ok("Senha redefinida com sucesso.");
    }

    [HttpPost("registrar")]
    public async Task<IActionResult> Registrar([FromBody] Usuario request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.SenhaHash) ||
            string.IsNullOrWhiteSpace(request.Nome))
            return BadRequest("Nome, email/login e senha são obrigatórios.");

        using var conn = new NpgsqlConnection(_connectionString);

        var senhaHash = GerarHash(request.SenhaHash.Trim());

        await conn.ExecuteAsync(@"
            INSERT INTO Usuarios 
            (id_filial, nome, email, senha_hash, perfil, ativo, criado_em, primeiro_acesso, IdEmpresa)
            VALUES 
            (@IdFilial, @Nome, @Email, @SenhaHash, @Perfil, 1, GETDATE(), 1, @IdEmpresa)",
            new
            {
                request.IdFilial,
                request.Nome,
                Email = request.Email.Trim(),
                SenhaHash = senhaHash,
                Perfil = string.IsNullOrWhiteSpace(request.Perfil) ? "OPERADOR" : request.Perfil,
                request.IdEmpresa
            });

        return Ok("Usuário criado com sucesso.");
    }

    private static string GerarHash(string senha)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(senha));
        return Convert.ToHexString(bytes).ToLower();
    }

    private string GerarToken(Usuario usuario)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, usuario.IdUsuario.ToString()),
            new Claim(ClaimTypes.Name, usuario.Nome),
            new Claim(ClaimTypes.Email, usuario.Email),
            new Claim(ClaimTypes.Role, usuario.Perfil),
            new Claim("id_filial", usuario.IdFilial?.ToString() ?? "0"),
            new Claim("id_empresa", usuario.IdEmpresa.ToString())
        };
        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(double.Parse(_config["Jwt:ExpiresInHours"]!)),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}