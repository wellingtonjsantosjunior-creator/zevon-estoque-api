using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Security.Cryptography;
using System.Text;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsuariosController : ControllerBase
{
    private readonly string _connectionString;

    public UsuariosController(string connectionString)
    {
        _connectionString = connectionString;
    }

    private int GetIdEmpresa() =>
        int.TryParse(User.FindFirst("id_empresa")?.Value, out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] int? idFilial)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new SqlConnection(_connectionString);
        var usuarios = await conn.QueryAsync(@"
            SELECT
                u.id_usuario AS idUsuario,
                u.id_filial AS idFilial,
                f.nome AS filial,
                u.nome,
                u.email,
                u.perfil,
                u.ativo,
                u.criado_em AS criadoEm
            FROM Usuarios u
            LEFT JOIN Filiais f ON u.id_filial = f.id_filial
            WHERE u.ativo = 1
              AND u.IdEmpresa = @IdEmpresa
              AND (@IdFilial IS NULL OR u.id_filial = @IdFilial)
            ORDER BY u.nome",
            new { IdFilial = idFilial, IdEmpresa = idEmpresa });
        return Ok(usuarios);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> BuscarPorId(int id)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new SqlConnection(_connectionString);
        var usuario = await conn.QueryFirstOrDefaultAsync(@"
            SELECT
                u.id_usuario AS idUsuario,
                u.id_filial AS idFilial,
                f.nome AS filial,
                u.nome,
                u.email,
                u.perfil,
                u.ativo,
                u.criado_em AS criadoEm
            FROM Usuarios u
            LEFT JOIN Filiais f ON u.id_filial = f.id_filial
            WHERE u.id_usuario = @Id
              AND u.IdEmpresa = @IdEmpresa",
            new { Id = id, IdEmpresa = idEmpresa });
        if (usuario == null) return NotFound("Usuário não encontrado.");
        return Ok(usuario);
    }

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] UsuarioRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Nome))
            return BadRequest("Nome é obrigatório.");
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Email é obrigatório.");
        if (string.IsNullOrWhiteSpace(request.Senha))
            return BadRequest("Senha é obrigatória.");

        var idEmpresa = GetIdEmpresa();
        using var conn = new SqlConnection(_connectionString);

        // Verifica se email já existe na mesma empresa
        var existe = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(1) FROM Usuarios WHERE email = @Email AND IdEmpresa = @IdEmpresa",
            new { request.Email, IdEmpresa = idEmpresa });
        if (existe > 0) return BadRequest("Email já cadastrado.");

        var senhaHash = GerarHash(request.Senha);

        await conn.ExecuteAsync(@"
            INSERT INTO Usuarios (id_filial, nome, email, senha_hash, perfil, ativo, criado_em, primeiro_acesso, IdEmpresa)
            VALUES (@IdFilial, @Nome, @Email, @SenhaHash, @Perfil, 1, GETDATE(), 1, @IdEmpresa)",
            new
            {
                request.IdFilial,
                request.Nome,
                request.Email,
                SenhaHash = senhaHash,
                Perfil = request.Perfil.ToUpper(),
                IdEmpresa = idEmpresa
            });

        return Ok("Usuário cadastrado com sucesso.");
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Atualizar(int id, [FromBody] UsuarioRequest request)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new SqlConnection(_connectionString);

        if (!string.IsNullOrWhiteSpace(request.Senha))
        {
            var senhaHash = GerarHash(request.Senha);
            await conn.ExecuteAsync(@"
                UPDATE Usuarios
                SET id_filial = @IdFilial,
                    nome = @Nome,
                    email = @Email,
                    senha_hash = @SenhaHash,
                    perfil = @Perfil
                WHERE id_usuario = @Id
                  AND IdEmpresa = @IdEmpresa",
                new
                {
                    Id = id,
                    request.IdFilial,
                    request.Nome,
                    request.Email,
                    SenhaHash = senhaHash,
                    Perfil = request.Perfil.ToUpper(),
                    IdEmpresa = idEmpresa
                });
        }
        else
        {
            await conn.ExecuteAsync(@"
                UPDATE Usuarios
                SET id_filial = @IdFilial,
                    nome = @Nome,
                    email = @Email,
                    perfil = @Perfil
                WHERE id_usuario = @Id
                  AND IdEmpresa = @IdEmpresa",
                new
                {
                    Id = id,
                    request.IdFilial,
                    request.Nome,
                    request.Email,
                    Perfil = request.Perfil.ToUpper(),
                    IdEmpresa = idEmpresa
                });
        }

        return Ok("Usuário atualizado com sucesso.");
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Inativar(int id)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE Usuarios SET ativo = 0 WHERE id_usuario = @Id AND IdEmpresa = @IdEmpresa",
            new { Id = id, IdEmpresa = idEmpresa });
        return Ok("Usuário inativado.");
    }

    [HttpPut("{id}/fcm-token")]
    public async Task<IActionResult> AtualizarFcmToken(int id, [FromBody] FcmTokenRequest request)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE Usuarios SET fcm_token = @Token WHERE id_usuario = @Id",
            new { Id = id, Token = request.Token });
        return Ok("Token FCM atualizado.");
    }

    private static string GerarHash(string senha)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(senha));
        return Convert.ToHexString(bytes).ToLower();
    }
}

public class UsuarioRequest
{
    public int? IdFilial { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Senha { get; set; }
    public string Perfil { get; set; } = "OPERADOR";
}

public class FcmTokenRequest
{
    public string Token { get; set; } = string.Empty;
}