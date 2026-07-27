using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificacoesController : ControllerBase
{
    private readonly string _connectionString;

    public NotificacoesController(string connectionString)
    {
        _connectionString = connectionString;
    }

    [HttpGet("{idUsuario}")]
    public async Task<IActionResult> Listar(int idUsuario)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        
        var lista = await conn.QueryAsync(@"
            SELECT
                id_notificacao AS idNotificacao,
                titulo,
                corpo,
                lida,
                criado_em AS criadoEm
            FROM Notificacoes
            WHERE id_usuario = @IdUsuario
            ORDER BY criado_em DESC",
            new { IdUsuario = idUsuario });
        return Ok(lista);
    }

    [HttpGet("{idUsuario}/nao-lidas")]
    public async Task<IActionResult> ContarNaoLidas(int idUsuario)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Notificacoes WHERE id_usuario = @IdUsuario AND lida = 0",
            new { IdUsuario = idUsuario });
        return Ok(new { total = count });
    }

    [HttpPut("{idUsuario}/marcar-lidas")]
    public async Task<IActionResult> MarcarLidas(int idUsuario)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE Notificacoes SET lida = 1 WHERE id_usuario = @IdUsuario AND lida = 0",
            new { IdUsuario = idUsuario });
        return Ok();
    }

    [HttpDelete("{idUsuario}")]
    public async Task<IActionResult> Limpar(int idUsuario)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM Notificacoes WHERE id_usuario = @IdUsuario",
            new { IdUsuario = idUsuario });
        return Ok();
    }
}