namespace ZevonEstoque.Models;

public class Movimentacao
{
    public int IdMovimentacao { get; set; }
    public int IdProduto { get; set; }
    public int IdFilial { get; set; }
    public int? IdPrateleira { get; set; }
    public int? IdUsuario { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public int Quantidade { get; set; }
    public int SaldoApos { get; set; }
    public DateTime DataHora { get; set; }
    public string? Observacao { get; set; }
    public bool OrigemScan { get; set; }
}

public class EntradaRequest
{
    public int IdProduto { get; set; }
    public int IdFilial { get; set; }
    public int? IdPrateleira { get; set; }
    public int IdUsuario { get; set; }
    public int Quantidade { get; set; }
    public string? Observacao { get; set; }
}

public class SaidaRequest
{
    public string CodigoPrateleira { get; set; } = string.Empty;
    public int IdProduto { get; set; }
    public int IdUsuario { get; set; }
    public int Quantidade { get; set; }
    public string? Observacao { get; set; }
    public int? IdRequisicao { get; set; }
}

public class KardexFiltro
{
    public int? IdFilial { get; set; }
    public int? IdProduto { get; set; }
    public DateTime? DataInicio { get; set; }
    public DateTime? DataFim { get; set; }
}