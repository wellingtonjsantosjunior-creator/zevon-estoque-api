using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FornecedoresController : ControllerBase
{
    private readonly string _connectionString;

    public FornecedoresController(string connectionString)
    {
        _connectionString = connectionString;
    }

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var fornecedores = await conn.QueryAsync(@"
            SELECT
                id_fornecedor AS ""idFornecedor"",
                nome,
                telefone,
                email,
                cnpj
            FROM Fornecedores
            ORDER BY nome");

        return Ok(fornecedores);
    }

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] FornecedorRequest fornecedor)
    {
        using var conn =new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            INSERT INTO Fornecedores (nome, telefone, email, cnpj)
            VALUES (@Nome, @Telefone, @Email, @Cnpj)", fornecedor);

        return Ok("Fornecedor cadastrado com sucesso.");
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Atualizar(int id, [FromBody] FornecedorRequest fornecedor)
    {
        using var conn =new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Fornecedores
            SET nome = @Nome,
                telefone = @Telefone,
                email = @Email,
                cnpj = @Cnpj
            WHERE id_fornecedor = @Id",
            new
            {
                Id = id,
                fornecedor.Nome,
                fornecedor.Telefone,
                fornecedor.Email,
                fornecedor.Cnpj
            });

        return Ok("Fornecedor atualizado com sucesso.");
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Excluir(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            DELETE FROM Fornecedores
            WHERE id_fornecedor = @Id",
            new { Id = id });

        return Ok("Fornecedor excluído com sucesso.");
    }
}

public class FornecedorRequest
{
    public string Nome { get; set; } = string.Empty;
    public string? Telefone { get; set; }
    public string? Email { get; set; }
    public string? Cnpj { get; set; }
}