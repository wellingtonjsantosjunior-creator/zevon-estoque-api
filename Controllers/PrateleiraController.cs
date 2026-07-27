using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PrateleirasController : ControllerBase
{
    private readonly string _connectionString;

    public PrateleirasController(string connectionString)
    {
        _connectionString = connectionString;
    }

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] int? idFilial)
    {
        using var conn = new SqlConnection(_connectionString);

        var prateleiras = await conn.QueryAsync(@"
            SELECT
                p.id_prateleira AS idPrateleira,
                p.id_filial AS idFilial,
                f.nome AS filial,
                p.codigo_barras AS codigoBarras,
                p.descricao,
                p.corredor,
                p.nivel,
                p.ativo
            FROM Prateleiras p
            INNER JOIN Filiais f ON p.id_filial = f.id_filial
            WHERE (@IdFilial IS NULL OR p.id_filial = @IdFilial)
            ORDER BY f.nome, p.descricao",
            new { IdFilial = idFilial });

        return Ok(prateleiras);
    }

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] PrateleiraRequest prateleira)
    {
        using var conn = new SqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            INSERT INTO Prateleiras
            (id_filial, codigo_barras, descricao, corredor, nivel, ativo)
            VALUES
            (@IdFilial, @CodigoBarras, @Descricao, @Corredor, @Nivel, 1)",
            prateleira);

        return Ok("Prateleira cadastrada com sucesso.");
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Atualizar(
        int id,
        [FromBody] PrateleiraRequest prateleira)
    {
        using var conn = new SqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Prateleiras
            SET id_filial = @IdFilial,
                codigo_barras = @CodigoBarras,
                descricao = @Descricao,
                corredor = @Corredor,
                nivel = @Nivel
            WHERE id_prateleira = @Id",
            new
            {
                Id = id,
                prateleira.IdFilial,
                prateleira.CodigoBarras,
                prateleira.Descricao,
                prateleira.Corredor,
                prateleira.Nivel
            });

        return Ok("Prateleira atualizada com sucesso.");
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Inativar(int id)
    {
        using var conn = new SqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Prateleiras
            SET ativo = 0
            WHERE id_prateleira = @Id",
            new { Id = id });

        return Ok("Prateleira inativada com sucesso.");
    }
}

public class PrateleiraRequest
{
    public int IdFilial { get; set; }
    public string CodigoBarras { get; set; } = string.Empty;
    public string? Descricao { get; set; }
    public string? Corredor { get; set; }
    public string? Nivel { get; set; }
}