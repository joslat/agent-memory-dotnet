//! The matchers of FORMAT.md, as the reference runner (native/gate/runner/gate_runner.py) applies them.

use serde_json::{Number, Value};

use crate::values::{get_pointer, python_json};

/// The ids of a recall's facts and preferences, sorted and unique (`$ids`).
fn recalled_ids(result: &Value) -> Value {
    let mut ids: Vec<String> = ["facts", "preferences"]
        .iter()
        .filter_map(|key| result.get(*key).and_then(Value::as_array))
        .flatten()
        .filter_map(|item| item.get("id").and_then(Value::as_str).map(str::to_owned))
        .collect();
    ids.sort();
    ids.dedup();
    Value::Array(ids.into_iter().map(Value::String).collect())
}

pub fn short(value: Option<&Value>) -> String {
    let Some(value) = value else { return "(missing)".into() };
    let text = value.to_string();
    if text.chars().count() <= 300 {
        return text;
    }
    text.chars().take(297).collect::<String>() + "..."
}

fn truthy(value: &Value) -> bool {
    match value {
        Value::Null => false,
        Value::Bool(flag) => *flag,
        Value::Number(number) => number.as_f64() != Some(0.0),
        Value::String(text) => !text.is_empty(),
        Value::Array(items) => !items.is_empty(),
        Value::Object(map) => !map.is_empty(),
    }
}

fn is_operator_object(expected: &Value) -> bool {
    match expected {
        Value::Object(map) => !map.is_empty() && map.keys().all(|key| key.starts_with('$') && key != "$ids"),
        _ => false,
    }
}

fn integer(number: &Number) -> Option<i128> {
    number
        .as_i64()
        .map(i128::from)
        .or_else(|| number.as_u64().map(i128::from))
}

fn numbers_equal(a: &Number, b: &Number) -> bool {
    match (integer(a), integer(b)) {
        (Some(a), Some(b)) => a == b,
        _ => a.as_f64() == b.as_f64(),
    }
}

fn scalars_equal(actual: &Value, expected: &Value) -> bool {
    match (actual, expected) {
        (Value::Number(a), Value::Number(b)) => numbers_equal(a, b),
        _ => actual == expected,
    }
}

/// Whether `actual` matches `expected`.
pub fn matches(actual: &Value, expected: &Value) -> bool {
    check(actual, expected, "").is_ok()
}

fn at(path: &str) -> &str {
    if path.is_empty() {
        "/"
    } else {
        path
    }
}

/// `Err` naming the first difference. Objects match partially, lists whole and in order.
pub fn check(actual: &Value, expected: &Value, path: &str) -> Result<(), String> {
    if is_operator_object(expected) {
        for (operator, operand) in expected.as_object().into_iter().flatten() {
            operate(actual, operator, operand, path)?;
        }
        return Ok(());
    }
    match expected {
        Value::Object(wanted) => {
            let Value::Object(object) = actual else {
                return Err(format!("{}: expected an object, got {}", at(path), short(Some(actual))));
            };
            for (key, wanted) in wanted {
                if key.starts_with('$') && key != "$ids" {
                    operate(actual, key, wanted, path)?;
                    continue;
                }
                if key == "$ids" {
                    let wanted = match wanted {
                        Value::Array(items) => {
                            let mut items = items.clone();
                            items.sort_by_key(|item| item.to_string());
                            Value::Array(items)
                        }
                        other => other.clone(),
                    };
                    check(&recalled_ids(actual), &wanted, &format!("{path}/$ids"))?;
                    continue;
                }
                let (value, where_) = if key.starts_with('/') {
                    (get_pointer(actual, key), format!("{path}{key}"))
                } else {
                    (object.get(key), format!("{path}/{key}"))
                };
                if wanted.get("$absent").is_some_and(truthy) {
                    if value.is_some() {
                        return Err(format!("{where_}: expected absent, got {}", short(value)));
                    }
                    continue;
                }
                if wanted.get("$empty").is_some_and(truthy) {
                    let empty = |value: &Value| match value {
                        Value::Null => true,
                        Value::String(text) => text.is_empty(),
                        Value::Array(items) => items.is_empty(),
                        Value::Object(map) => map.is_empty(),
                        _ => false,
                    };
                    if value.is_some_and(|value| !empty(value)) {
                        return Err(format!("{where_}: expected absent or empty, got {}", short(value)));
                    }
                    continue;
                }
                let Some(value) = value else {
                    return Err(format!("{where_}: missing (expected {})", short(Some(wanted))));
                };
                check(value, wanted, &where_)?;
            }
            Ok(())
        }
        Value::Array(wanted) => {
            match actual {
                Value::Array(items) if items.len() == wanted.len() => {}
                _ => {
                    return Err(format!(
                        "{}: expected {}, got {}",
                        at(path),
                        short(Some(expected)),
                        short(Some(actual))
                    ))
                }
            }
            for (i, (item, wanted)) in actual.as_array().into_iter().flatten().zip(wanted).enumerate() {
                check(item, wanted, &format!("{path}/{i}"))?;
            }
            Ok(())
        }
        _ if scalars_equal(actual, expected) => Ok(()),
        _ => Err(format!(
            "{}: expected {}, got {}",
            at(path),
            short(Some(expected)),
            short(Some(actual))
        )),
    }
}

fn number(value: &Value) -> Option<f64> {
    match value {
        Value::Number(number) => number.as_f64(),
        _ => None,
    }
}

fn operate(actual: &Value, operator: &str, operand: &Value, path: &str) -> Result<(), String> {
    let fail = |text: String| Err(format!("{}: {text}, got {}", at(path), short(Some(actual))));
    let items: Vec<&Value> = match operand {
        Value::Array(items) => items.iter().collect(),
        other => vec![other],
    };
    let contains = |item: &Value| match (actual, item) {
        (Value::String(text), Value::String(part)) => Some(text.contains(part.as_str())),
        (Value::String(_), _) => Some(false),
        (Value::Array(list), item) => Some(list.iter().any(|element| matches(element, item))),
        _ => None,
    };
    match operator {
        "$oneOf" => {
            if !items.iter().any(|option| matches(actual, option)) {
                return fail(format!("expected one of {}", short(Some(operand))));
            }
        }
        "$not" => {
            if matches(actual, operand) {
                return fail(format!("expected anything but {}", short(Some(operand))));
            }
        }
        "$contains" => {
            for item in items {
                match contains(item) {
                    Some(true) => {}
                    Some(false) if actual.is_string() => {
                        return fail(format!("expected text containing {}", short(Some(item))))
                    }
                    Some(false) => return fail(format!("expected a list containing {}", short(Some(item)))),
                    None => return fail("expected text or a list".into()),
                }
            }
        }
        "$notContains" => {
            for item in items {
                if contains(item) == Some(true) {
                    return fail(format!("expected nothing containing {}", short(Some(item))));
                }
            }
        }
        "$setEq" => {
            let sorted = |list: &[Value]| {
                let mut texts: Vec<String> = list.iter().map(Value::to_string).collect();
                texts.sort();
                texts
            };
            let same = match (actual, operand) {
                (Value::Array(actual), Value::Array(operand)) => sorted(actual) == sorted(operand),
                _ => false,
            };
            if !same {
                return fail(format!("expected the set {}", short(Some(operand))));
            }
        }
        "$gt" | "$gte" | "$lt" => {
            let bound = number(operand).ok_or_else(|| format!("{operator} takes a number"))?;
            let holds = number(actual).is_some_and(|value| match operator {
                "$gt" => value > bound,
                "$gte" => value >= bound,
                _ => value < bound,
            });
            if !holds {
                let what = match operator {
                    "$gt" => "more than",
                    "$gte" => "at least",
                    _ => "less than",
                };
                return fail(format!("expected {what} {operand}"));
            }
        }
        "$textExcludes" => {
            let text = operand.as_str().unwrap_or_default();
            if python_json(actual, false).contains(text) {
                return fail(format!("expected no text {} anywhere", short(Some(operand))));
            }
        }
        "$len" => {
            let length = match actual {
                Value::Array(items) => Some(items.len()),
                Value::Object(map) => Some(map.len()),
                Value::String(text) => Some(text.chars().count()),
                _ => None,
            };
            if length.map(|length| length as u64) != operand.as_u64() {
                return fail(format!("expected length {operand}"));
            }
        }
        "$type" => {
            let holds = match operand.as_str().unwrap_or_default() {
                "string" => actual.is_string(),
                "number" => actual.is_number(),
                "integer" => actual.is_i64() || actual.is_u64(),
                "boolean" => actual.is_boolean(),
                "object" => actual.is_object(),
                "array" => actual.is_array(),
                "null" => actual.is_null(),
                other => return Err(format!("unknown $type {other}")),
            };
            if !holds {
                return fail(format!("expected a {}", operand.as_str().unwrap_or_default()));
            }
        }
        "$startsWith" => {
            let prefix = operand.as_str().unwrap_or_default();
            if !actual.as_str().is_some_and(|text| text.starts_with(prefix)) {
                return fail(format!("expected text starting with {}", short(Some(operand))));
            }
        }
        "$all" => match actual {
            Value::Array(list) if !list.is_empty() => {
                for (i, item) in list.iter().enumerate() {
                    check(item, operand, &format!("{path}/{i}"))?;
                }
            }
            _ => return fail("expected a non-empty list".into()),
        },
        "$absent" => {
            if truthy(operand) {
                return fail("expected absent".into());
            }
        }
        other => return Err(format!("unknown matcher {other}")),
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn objects_match_partially_and_lists_whole() {
        assert!(matches(&json!({"status": "OK", "handle": 5}), &json!({"status": "OK"})));
        assert!(!matches(&json!([1, 2]), &json!([1])));
        assert!(matches(&json!({"a": {"b": 1.0}}), &json!({"/a/b": 1})));
        assert!(!matches(&json!(true), &json!(1)), "true is not 1");
    }

    #[test]
    fn matchers_hold_as_the_reference_runner_says() {
        assert!(matches(&json!("OK"), &json!({"$not": "BUSY"})));
        assert!(matches(
            &json!("timeout"),
            &json!({"$oneOf": ["unavailable", "timeout"]})
        ));
        let features = json!(["am_v1_wait", "get_context.maxConnectionFacts"]);
        assert!(matches(
            &json!({"features": features}),
            &json!({"features": {"$contains": ["am_v1_wait"], "$notContains": "remember.origin"}})
        ));
        assert!(matches(
            &json!([{"status": "ok"}, {"status": "ok"}]),
            &json!({"$all": {"status": "ok"}})
        ));
        assert!(!matches(&json!([]), &json!({"$all": {"status": "ok"}})));
        assert!(matches(
            &json!({"a": 1}),
            &json!({"b": {"$absent": true}, "c": {"$empty": true}})
        ));
        assert!(matches(&json!([3, 1, 2]), &json!({"$setEq": [1, 2, 3]})));
        assert!(matches(&json!(5), &json!({"$gt": 4, "$lt": 6, "$type": "integer"})));
        assert!(matches(&json!("abc"), &json!({"$len": 3, "$startsWith": "ab"})));
        assert!(matches(&json!({"x": "secret"}), &json!({"$textExcludes": "password"})));
        let recall = json!({"facts": [{"id": "f-2"}, {"id": "f-1"}], "preferences": [{"id": "p-1"}]});
        assert!(matches(&recall, &json!({"$ids": ["p-1", "f-2", "f-1"]})));
    }
}
