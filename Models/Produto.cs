namespace ZevonEstoque.Models;

public class Produto
{
    public int IdProduto { get; set; }
    public int? IdCategoria { get; set; }
    public int? IdFornecedor { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string? CodigoSku { get; set; }
    public string? Descricao { get; set; }
    public decimal? PrecoCusto { get; set; }
    public decimal? PrecoVenda { get; set; }
    public string Unidade { get; set; } = "UN";
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
}