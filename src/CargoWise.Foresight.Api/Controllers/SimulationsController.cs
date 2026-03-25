using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using CargoWise.Foresight.Api.Middleware;
using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Api.Controllers;

[ApiController]
[Route("simulations")]
[Produces("application/json")]
public sealed class SimulationsController : ControllerBase
{
    private readonly ISimulationEngine _engine;
    private readonly IExplanationService _explanationService;
    private readonly IMitigationService _mitigationService;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<SimulationsController> _logger;

    public SimulationsController(
        ISimulationEngine engine,
        IExplanationService explanationService,
        IMitigationService mitigationService,
        MetricsCollector metrics,
        ILogger<SimulationsController> logger)
    {
        _engine = engine;
        _explanationService = explanationService;
        _mitigationService = mitigationService;
        _metrics = metrics;
        _logger = logger;
    }

    [HttpPost("run")]
    [ProducesResponseType(typeof(SimulationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RunSimulation([FromBody] SimulationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return BadRequest(new ProblemDetails { Title = "requestId is required" });

        if (request.Baseline?.Shipment == null)
            return BadRequest(new ProblemDetails { Title = "baseline.shipment is required" });

        if (request.ChangeSet == null)
            return BadRequest(new ProblemDetails { Title = "changeSet is required" });

        if (request.SimulationRuns is < 1 or > 100_000)
            return BadRequest(new ProblemDetails { Title = "simulationRuns must be between 1 and 100,000" });

        var sw = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Simulation requested: {RequestId}, changeType={ChangeType}",
                request.RequestId, request.ChangeSet.ChangeType);

            var result = await _engine.RunAsync(request, ct);

            var enhancedRisks = await _mitigationService.EnhanceMitigationsAsync(result.Risks, request, ct);
            var enhancedRecs = await _mitigationService.EnhanceRecommendationsAsync(result.Recommendations, request, result, ct);
            result = result with { Risks = enhancedRisks, Recommendations = enhancedRecs };

            sw.Stop();
            _metrics.RecordSimulation(request.RequestId, sw.Elapsed, true);

            return Ok(result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metrics.RecordSimulation(request.RequestId, sw.Elapsed, false);
            _logger.LogError(ex, "Simulation {RequestId} failed", request.RequestId);

            return StatusCode(500, new ProblemDetails
            {
                Title = "Simulation failed",
                Detail = "An internal error occurred during simulation. Check logs for details.",
                Status = 500
            });
        }
    }

    [HttpPost("explain")]
    [ProducesResponseType(typeof(ExplanationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Explain([FromBody] ExplanationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return BadRequest(new ProblemDetails { Title = "requestId is required" });

        if (request.SimulationResult == null)
            return BadRequest(new ProblemDetails { Title = "simulationResult is required" });

        try
        {
            var response = await _explanationService.ExplainAsync(request, ct);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explanation {RequestId} failed", request.RequestId);
            return StatusCode(500, new ProblemDetails
            {
                Title = "Explanation generation failed",
                Detail = "An error occurred generating the explanation.",
                Status = 500
            });
        }
    }
}
