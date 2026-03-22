using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Api.Controllers;

[ApiController]
[Route("scenarios")]
[Produces("application/json")]
public sealed class ScenariosController : ControllerBase
{
    // In-memory store for MVP; swap for a real persistence layer later
    private static readonly ConcurrentDictionary<string, Scenario> Store = new();

    [HttpPost]
    [ProducesResponseType(typeof(Scenario), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult SaveScenario([FromBody] Scenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.ScenarioId))
            return BadRequest(new ProblemDetails { Title = "scenarioId is required" });

        if (string.IsNullOrWhiteSpace(scenario.Name))
            return BadRequest(new ProblemDetails { Title = "name is required" });

        if (!Store.TryAdd(scenario.ScenarioId, scenario))
            return Conflict(new ProblemDetails { Title = $"Scenario '{scenario.ScenarioId}' already exists" });

        return CreatedAtAction(nameof(GetScenario), new { id = scenario.ScenarioId }, scenario);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Scenario), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetScenario(string id)
    {
        if (Store.TryGetValue(id, out var scenario))
            return Ok(scenario);
        return NotFound();
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<Scenario>), StatusCodes.Status200OK)]
    public IActionResult ListScenarios()
    {
        return Ok(Store.Values.OrderByDescending(s => s.CreatedAt).ToList());
    }
}
