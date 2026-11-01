//! The format registry (feature 01): spec/registry.json, the one definition of every closed shape, list, limit and
//! profile, embedded at build time from its real path in the repository. No copy is kept here.

use alloc::collections::BTreeMap;
use alloc::string::{String, ToString};
use alloc::vec::Vec;

use crate::json::{registry_text, Obj, Value};
use crate::{refused, Refused, R};

/// The registry's bytes, as the build found them.
pub const REGISTRY_JSON: &str = include_str!("../../../../spec/registry.json");

pub struct Profile {
    pub name: String,
    pub implemented: bool,
    pub files: Vec<String>,
    pub fields: Vec<String>,
    pub kinds: Vec<String>,
    pub extensions: Vec<String>,
    pub values: Vec<(String, Vec<String>)>,
}

pub struct Registry {
    shapes: BTreeMap<String, Vec<String>>,
    pub manifest_fields: Vec<String>,
    pub kinds: Vec<String>,
    pub tiers: Vec<String>,
    pub protections: Vec<String>,
    pub codes: Vec<String>,
    pub extensions: Vec<String>,
    pub string_fields: Vec<String>,
    pub defaults: Vec<(String, String)>,
    pub member_fields: Vec<String>,
    pub signing_roles: Vec<String>,
    pub key_roles: Vec<String>,
    pub access_kinds: Vec<String>,
    pub certificate_targets: Vec<String>,
    pub cost_targets: Vec<String>,
    pub measures: Vec<String>,
    pub baselines: Vec<String>,
    pub confidences: Vec<i64>,
    pub devices: Vec<String>,
    pub profiles: Vec<Profile>,
    pub display_refused: Vec<(u32, u32)>,
    pub dtypes: Vec<String>,
    pub distances: Vec<String>,
    pub erasure_reasons: Vec<String>,
    pub erasure_roles: Vec<String>,
    pub derivation_path: String,
    pub derivation_format: String,
    pub derivation_actions: Vec<String>,
    pub policy_path: String,
    pub policy_format: String,
    pub policy_verbs: Vec<String>,
}

fn bad(what: &str) -> Refused {
    refused!("the format registry is malformed: {what}")
}

fn at<'a>(v: &'a Value, path: &[&str]) -> R<&'a Value> {
    let mut cur = v;
    for p in path {
        cur = cur.as_obj().and_then(|o| o.get(p)).ok_or_else(|| bad(p))?;
    }
    Ok(cur)
}

fn strs(v: &Value, path: &[&str]) -> R<Vec<String>> {
    let a = at(v, path)?;
    a.as_strs().map(|x| x.into_iter().map(String::from).collect()).ok_or_else(|| bad(path.last().unwrap_or(&"")))
}

fn opt_strs(o: &Obj, key: &str) -> R<Vec<String>> {
    match o.get(key) {
        None => Ok(Vec::new()),
        Some(v) => v.as_strs().map(|x| x.into_iter().map(String::from).collect()).ok_or_else(|| bad(key)),
    }
}

fn keys(v: &Value, path: &[&str]) -> R<Vec<String>> {
    Ok(at(v, path)?.as_obj().ok_or_else(|| bad("an object"))?.keys().map(String::from).collect())
}

fn text(v: &Value, path: &[&str]) -> R<String> {
    at(v, path)?.as_str().map(String::from).ok_or_else(|| bad(path.last().unwrap_or(&"")))
}

impl Registry {
    /// The embedded registry, read.
    pub fn load() -> R<Registry> {
        Registry::parse(REGISTRY_JSON.as_bytes())
    }

    pub fn parse(bytes: &[u8]) -> R<Registry> {
        let r = registry_text(bytes)?;
        if !at(&r, &["format"])?.is_str("ampack.registry/1") {
            return Err(refused!("the format registry is not ampack.registry/1"));
        }
        let mut shapes = BTreeMap::new();
        for (name, doc) in &at(&r, &["documents"])?.as_obj().ok_or_else(|| bad("documents"))?.0 {
            shapes.insert(name.clone(), strs(doc, &["fields"])?);
        }
        let mut profiles = Vec::new();
        for (name, p) in &at(&r, &["profiles"])?.as_obj().ok_or_else(|| bad("profiles"))?.0 {
            let o = p.as_obj().ok_or_else(|| bad("a profile"))?;
            let mut values = Vec::new();
            if let Some(v) = o.get("values") {
                for (field, list) in &v.as_obj().ok_or_else(|| bad("profile values"))?.0 {
                    values.push((field.clone(), list.as_strs().ok_or_else(|| bad("profile values"))?.into_iter().map(String::from).collect()));
                }
            }
            profiles.push(Profile {
                name: name.clone(),
                implemented: o.get("implemented").and_then(Value::as_bool).ok_or_else(|| bad("implemented"))?,
                files: opt_strs(o, "files")?,
                fields: opt_strs(o, "fields")?,
                kinds: opt_strs(o, "kinds")?,
                extensions: opt_strs(o, "extensions")?,
                values,
            });
        }
        let mut display_refused = Vec::new();
        for pair in at(&r, &["text", "displayRefused"])?.as_arr().ok_or_else(|| bad("displayRefused"))? {
            let p = pair.as_strs().filter(|p| p.len() == 2).ok_or_else(|| bad("displayRefused"))?;
            let a = u32::from_str_radix(p[0], 16).map_err(|_| bad("displayRefused"))?;
            let b = u32::from_str_radix(p[1], 16).map_err(|_| bad("displayRefused"))?;
            display_refused.push((a, b));
        }
        display_refused.sort();
        let mut defaults = Vec::new();
        for (k, v) in &at(&r, &["manifest", "defaults"])?.as_obj().ok_or_else(|| bad("defaults"))?.0 {
            defaults.push((k.clone(), v.as_str().ok_or_else(|| bad("defaults"))?.to_string()));
        }
        // A host-only target (feature 12: noHarm) is decided by the host alone; no certificate certifies it.
        let host_only = match at(&r, &["trial"])?.as_obj().and_then(|t| t.get("hostOnly")) {
            None => Vec::new(),
            Some(_) => strs(&r, &["trial", "hostOnly"])?,
        };
        let mut certificate_targets: Vec<String> = keys(&r, &["trial", "targets"])?.into_iter().filter(|t| !holds(&host_only, t)).collect();
        let cost_targets = keys(&r, &["trial", "costTargets"])?;
        certificate_targets.extend(cost_targets.iter().cloned());
        let confidences = at(&r, &["units", "fields", "confidencePermille", "values"])?
            .as_arr()
            .ok_or_else(|| bad("confidencePermille"))?
            .iter()
            .map(|v| v.as_int().ok_or_else(|| bad("confidencePermille")))
            .collect::<R<Vec<i64>>>()?;
        Ok(Registry {
            shapes,
            manifest_fields: strs(&r, &["manifest", "fields"])?,
            kinds: strs(&r, &["manifest", "values", "kind"])?,
            tiers: strs(&r, &["manifest", "values", "tier"])?,
            protections: strs(&r, &["manifest", "values", "protection"])?,
            codes: strs(&r, &["manifest", "values", "code"])?,
            extensions: strs(&r, &["manifest", "extensions"])?,
            string_fields: strs(&r, &["manifest", "stringFields"])?,
            defaults,
            member_fields: strs(&r, &["members", "fields"])?,
            signing_roles: strs(&r, &["roles", "signing"])?,
            key_roles: strs(&r, &["roles", "keys"])?,
            access_kinds: strs(&r, &["federation", "accessKinds"])?,
            certificate_targets,
            cost_targets,
            measures: strs(&r, &["certificate", "measures"])?,
            baselines: strs(&r, &["certificate", "baselines"])?,
            confidences,
            devices: strs(&r, &["paths", "deviceNames"])?,
            profiles,
            display_refused,
            dtypes: strs(&r, &["embeddings", "dataTypes"])?,
            distances: strs(&r, &["embeddings", "distances"])?,
            erasure_reasons: strs(&r, &["erasure", "reasons"])?,
            erasure_roles: strs(&r, &["erasure", "roles"])?,
            derivation_path: text(&r, &["derivation", "path"])?,
            derivation_format: text(&r, &["derivation", "format"])?,
            derivation_actions: strs(&r, &["derivation", "actions"])?,
            policy_path: text(&r, &["policy", "path"])?,
            policy_format: text(&r, &["policy", "format"])?,
            policy_verbs: strs(&r, &["policy", "verbs"])?,
        })
    }

    /// The closed shape of a document the registry names (its fields). An unknown name is a defect of this reader.
    pub fn shape(&self, name: &str) -> &[String] {
        self.shapes.get(name).map(|v| v.as_slice()).unwrap_or_else(|| panic!("the registry names no document '{name}'"))
    }

    pub fn default_of(&self, field: &str) -> Option<&str> {
        self.defaults.iter().find(|(k, _)| k == field).map(|(_, v)| v.as_str())
    }
}

/// Whether a list of names holds this one.
pub fn holds(list: &[String], name: &str) -> bool {
    list.iter().any(|x| x == name)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_embedded_registry_reads() {
        let r = Registry::load().unwrap();
        assert!(holds(&r.kinds, "knowledge"));
        assert!(r.shape("envelope").len() == 3);
        assert!(r.profiles.iter().any(|p| p.name == "federation"));
        assert!(r.display_safe("hello"));
        assert!(!r.display_safe("a\u{202e}b"));
    }
}
