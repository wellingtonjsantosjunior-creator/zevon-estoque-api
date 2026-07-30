using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FiliaisController : ControllerBase
{
    private readonly string _connectionString;

    public FiliaisController(string connectionString)
    {
        _connectionString = connectionString;
    }

    private int GetIdEmpresa() =>
        int.TryParse(User.FindFirst("id_empresa")?.Value, out var id) ? id : 0;

    [HttpGet("debug-token")]
    [Authorize]
    public IActionResult DebugToken()
    {
        var claims = User.Claims.Select(c => new { c.Type, c.Value });
        return Ok(new { idEmpresa = GetIdEmpresa(), claims });
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Listar()
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        var filiais = await conn.QueryAsync(@"
            SELECT
                id_filial  AS ""idFilial"",
                nome,
                cnpj,
                endereco,
                cidade,
                ativo,
                criado_em  AS ""criadoEm"",
                IdEmpresa  AS ""idEmpresa""
            FROM Filiais
            WHERE ativo IS TRUE
              AND IdEmpresa = @IdEmpresa
            ORDER BY nome",
            new { IdEmpresa = idEmpresa });

        return Ok(filiais);
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Criar([FromBody] FilialRequest filial)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            INSERT INTO Filiais
            (nome, cnpj, endereco, cidade, ativo, criado_em, IdEmpresa)
            VALUES
            (@Nome, @Cnpj, @Endereco, @Cidade, true, NOW(), @IdEmpresa)",
            new
            {
                filial.Nome,
                filial.Cnpj,
                filial.Endereco,
                filial.Cidade,
                IdEmpresa = idEmpresa
            });

        return Ok("Filial cadastrada com sucesso.");
    }

    [HttpPut("{id}")]
[Authorize]
public async Task<IActionResult> Atualizar(int id, [FromBody] FilialRequest filial)
{
    var idEmpresa = GetIdEmpresa();
    using var conn = new NpgsqlConnection(_connectionString);

    await conn.ExecuteAsync(@"
        UPDATE Filiais
        SET nome = @Nome,
            cnpj = @Cnpj,
            endereco = @Endereco,
            cidade = @Cidade
        WHERE id_filial = @Id
          AND IdEmpresa = @IdEmpresa",
        new
        {
            Id = id,
            filial.Nome,
            filial.Cnpj,
            filial.Endereco,
            filial.Cidade,
            IdEmpresa = idEmpresa
        });

    return Ok("Filial atualizada com sucesso.");
}

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> Inativar(int id)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn =new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Filiais
            SET ativo = false
            WHERE id_filial = @Id
              AND IdEmpresa = @IdEmpresa",
            new { Id = id, IdEmpresa = idEmpresa });

        return Ok("Filial inativada com sucesso.");
    }
}

public class FilialRequest
{
    public string Nome { get; set; } = string.Empty;
    public string? Cnpj { get; set; }
    public string? Endereco { get; set; }
    public string? Cidade { get; set; }
}