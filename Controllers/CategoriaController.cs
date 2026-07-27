using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CategoriasController : ControllerBase
{
    private readonly string _connectionString;

    public CategoriasController(string connectionString)
    {
        _connectionString = connectionString;
    }

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        using var conn = new SqlConnection(_connectionString);

        var categorias = await conn.QueryAsync(@"
            SELECT
                id_categoria AS idCategoria,
                nome,
                descricao
            FROM Categorias
            ORDER BY nome");

        return Ok(categorias);
    }

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] CategoriaRequest categoria)
    {
        using var conn = new SqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            INSERT INTO Categorias (nome, descricao)
            VALUES (@Nome, @Descricao)", categoria);

        return Ok("Categoria cadastrada com sucesso.");
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Atualizar(int id, [FromBody] CategoriaRequest categoria)
    {
        using var conn = new SqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Categorias
            SET nome = @Nome,
                descricao = @Descricao
            WHERE id_categoria = @Id",
            new
            {
                Id = id,
                categoria.Nome,
                categoria.Descricao
            });

        return Ok("Categoria atualizada com sucesso.");
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Excluir(int id)
    {
        using var conn = new SqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            DELETE FROM Categorias
            WHERE id_categoria = @Id",
            new { Id = id });

        return Ok("Categoria excluída com sucesso.");
    }
}

public class CategoriaRequest
{
    public string Nome { get; set; } = string.Empty;
    public string? Descricao { get; set; }
}